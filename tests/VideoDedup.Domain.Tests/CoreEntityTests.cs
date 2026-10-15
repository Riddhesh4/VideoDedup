using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public sealed class CoreEntityTests
{
    [Fact]
    public void ScanRoot_requires_identity_path_and_nonblank_label()
    {
        FluentActions.Invoking(() => ScanRoot.Create(Guid.Empty, "Movies", "D:/Movies", ScanRootType.LocalFolder))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ScanRoot.Create(Guid.NewGuid(), " ", "D:/Movies", ScanRootType.LocalFolder))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ScanRoot.Create(Guid.NewGuid(), "Movies", " ", ScanRootType.LocalFolder))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScanRoot_normalizes_path_and_keeps_exclusions_immutable()
    {
        var patterns = new[] { "*.tmp", "cache/**" };
        var root = ScanRoot.Create(Guid.NewGuid(), " Movies ", @"D:\Movies\", ScanRootType.LocalFolder,
            includeSubfolders: true, excludedPatterns: patterns);
        patterns[0] = "*";

        root.Label.Should().Be("Movies");
        root.Path.Should().Be("D:/Movies");
        root.ExcludedPatterns.Should().Equal("*.tmp", "cache/**");
        FluentActions.Invoking(() => ((IList<string>)root.ExcludedPatterns)[0] = "*")
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void DriveDetails_validates_and_normalizes_stable_drive_metadata()
    {
        var drive = DriveDetails.Create(" volume-123 ", DriveKind.Usb, " Movies ", 1024);

        drive.VolumeSignature.Should().Be("volume-123");
        drive.Kind.Should().Be(DriveKind.Usb);
        drive.Label.Should().Be("Movies");
        drive.CapacityBytes.Should().Be(1024);
        FluentActions.Invoking(() => DriveDetails.Create(" ", DriveKind.Local)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DriveDetails.Create("vol", DriveKind.Usb, capacityBytes: -1)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => DriveDetails.Create("vol", (DriveKind)99)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => DriveDetails.Create("vol", DriveKind.Local, "bad\u0001label")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScanRoot_requires_drive_details_consistent_with_root_type()
    {
        var usb = DriveDetails.Create("usb-volume", DriveKind.Usb);
        var network = DriveDetails.Create("network-volume", DriveKind.Network);

        FluentActions.Invoking(() => ScanRoot.Create(Guid.NewGuid(), "USB", "E:/", ScanRootType.UsbDrive))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ScanRoot.Create(Guid.NewGuid(), "NAS", "//server/share", ScanRootType.NetworkShare))
            .Should().Throw<ArgumentException>();

        ScanRoot.Create(Guid.NewGuid(), "USB", "E:/", ScanRootType.UsbDrive, driveDetails: usb).DriveDetails.Should().Be(usb);
        ScanRoot.Create(Guid.NewGuid(), "NAS", "//server/share", ScanRootType.NetworkShare, driveDetails: network).DriveDetails.Should().Be(network);
    }

    [Fact]
    public void VideoFile_validates_ids_size_and_relative_path()
    {
        var rootId = Guid.NewGuid();
        FluentActions.Invoking(() => VideoFile.Create(Guid.Empty, rootId, "movie.mp4", 100, DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => VideoFile.Create(Guid.NewGuid(), Guid.Empty, "movie.mp4", 100, DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => VideoFile.Create(Guid.NewGuid(), rootId, "../movie.mp4", 100, DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => VideoFile.Create(Guid.NewGuid(), rootId, "movie.mp4", -1, DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void VideoFile_accepts_unknown_probe_metadata_without_fabricating_values()
    {
        var file = VideoFile.Create(Guid.NewGuid(), Guid.NewGuid(), "folder/movie.mkv", 0, DateTimeOffset.UnixEpoch);

        file.ScanState.Should().Be(ScanState.Discovered);
        file.Duration.Should().BeNull();
        file.Width.Should().BeNull();
        file.Height.Should().BeNull();
        file.HasAudio.Should().BeNull();
        file.Sha256.Should().BeNull();
    }

    [Fact]
    public void WithProbe_returns_a_probed_copy_without_mutating_discovered_record()
    {
        var file = VideoFile.Create(Guid.NewGuid(), Guid.NewGuid(), "movie.mkv", 100, DateTimeOffset.UnixEpoch);
        var probed = file.WithProbe(TimeSpan.FromSeconds(12), 1920, 1080, "h264", "aac", true);

        file.ScanState.Should().Be(ScanState.Discovered);
        file.Width.Should().BeNull();
        probed.ScanState.Should().Be(ScanState.Probed);
        probed.Duration.Should().Be(TimeSpan.FromSeconds(12));
        probed.Width.Should().Be(1920);
        probed.Height.Should().Be(1080);
        probed.HasAudio.Should().BeTrue();
        probed.VideoCodec.Should().Be("h264");
        probed.AudioCodec.Should().Be("aac");
    }

    [Fact]
    public void WithProbe_rejects_negative_duration_and_nonpositive_dimensions()
    {
        var file = VideoFile.Create(Guid.NewGuid(), Guid.NewGuid(), "movie.mkv", 100, DateTimeOffset.UnixEpoch);

        FluentActions.Invoking(() => file.WithProbe(TimeSpan.FromSeconds(-1), 1920, 1080, null, null, false))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => file.WithProbe(TimeSpan.Zero, 0, 1080, null, null, false))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void VideoFile_supports_every_scan_state_transition_immutably()
    {
        var initial = VideoFile.Create(Guid.NewGuid(), Guid.NewGuid(), "movie.mkv", 100, DateTimeOffset.UnixEpoch);
        var discovered = initial.ResetForRescan().MarkDiscovered();
        var probed = discovered.WithProbe(TimeSpan.FromSeconds(4), 1280, 720, "h264", null, false);
        var hashed = probed.WithHash(new string('a', 64));
        var fingerprint = VisualFingerprint.Create(TimeSpan.FromSeconds(1), 42, 0.8m, FingerprintVersion.Create(1, 0));
        var fingerprinted = hashed.WithFingerprints(new[] { fingerprint });
        var failed = fingerprinted.MarkFailed();
        var unsupported = fingerprinted.MarkUnsupported();
        var reset = failed.ResetForRescan();

        initial.ScanState.Should().Be(ScanState.Discovered);
        discovered.ScanState.Should().Be(ScanState.Discovered);
        probed.ScanState.Should().Be(ScanState.Probed);
        hashed.ScanState.Should().Be(ScanState.Hashed);
        fingerprinted.ScanState.Should().Be(ScanState.Fingerprinted);
        fingerprinted.VisualFingerprints.Should().ContainSingle().Which.Should().Be(fingerprint);
        failed.ScanState.Should().Be(ScanState.Failed);
        unsupported.ScanState.Should().Be(ScanState.Unsupported);
        reset.ScanState.Should().Be(ScanState.NotScanned);
        reset.Sha256.Should().BeNull();
        reset.VisualFingerprints.Should().BeEmpty();
        FluentActions.Invoking(() => failed.MarkUnsupported()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => initial.ResetForRescan().MarkFailed()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => initial.WithHash(new string('a', 64))).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => probed.WithFingerprints(new[] { fingerprint })).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WithFingerprints_rejects_empty_mixed_version_and_out_of_duration_data()
    {
        var file = VideoFile.Create(Guid.NewGuid(), Guid.NewGuid(), "movie.mkv", 100, DateTimeOffset.UnixEpoch)
            .WithProbe(TimeSpan.FromSeconds(2), 1920, 1080, null, null, false)
            .WithHash(new string('a', 64));
        var v1 = VisualFingerprint.Create(TimeSpan.FromSeconds(1), 1, 1m, FingerprintVersion.Create(1, 0));
        var v2 = VisualFingerprint.Create(TimeSpan.FromSeconds(1), 1, 1m, FingerprintVersion.Create(2, 0));
        var late = VisualFingerprint.Create(TimeSpan.FromSeconds(3), 1, 1m, FingerprintVersion.Create(1, 0));

        FluentActions.Invoking(() => file.WithFingerprints(Array.Empty<VisualFingerprint>())).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => file.WithFingerprints(new[] { v1, v2 })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => file.WithFingerprints(new[] { late })).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithHash_requires_lowercase_sha256_and_returns_hashed_copy()
    {
        var file = VideoFile.Create(Guid.NewGuid(), Guid.NewGuid(), "movie.mkv", 100, DateTimeOffset.UnixEpoch)
            .WithProbe(TimeSpan.FromSeconds(1), 1920, 1080, "h264", null, false);
        var digest = new string('A', 64);

        var hashed = file.WithHash(digest);

        hashed.Sha256.Should().Be(new string('A', 64).ToLowerInvariant());
        hashed.ScanState.Should().Be(ScanState.Hashed);
        file.Sha256.Should().BeNull();
        FluentActions.Invoking(() => file.WithHash("invalid"))
            .Should().Throw<ArgumentException>();
    }
}
