using System.Security.Cryptography;
using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Infrastructure;
using VideoDedup.Workers;

namespace VideoDedup.Infrastructure.Tests;

public sealed class MediaProcessingTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "VideoDedup.Media", Guid.NewGuid().ToString("N"));
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VideoDedup.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Solution not found.");
        }
    }
    private static string Probe => Path.Combine(Root, "tools", "media", "ffprobe.exe");
    private static string Ffmpeg => Path.Combine(Root, "tools", "media", "ffmpeg.exe");
    private static ProcessRunner Runner() => new([Probe, Ffmpeg]);
    private string TempFile(string name = "clip.mp4", byte[]? bytes = null)
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, name);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task Real_fixture_extracts_metadata_and_hash_is_exact_across_names()
    {
        var fixture = Path.Combine(Root, "TestFixtures", "generated", "sample.mkv");
        File.Exists(fixture).Should().BeTrue("run python scripts/prepare-media.py first");
        var path = TempFile("movie unicode \u2603 & echo injected.mkv", File.ReadAllBytes(fixture));
        var revision = MediaFileAccess.Revision(path);
        var probe = await new FfprobeMediaProbe(Runner(), Probe).ProbeAsync(path, revision, TestContext.Current.CancellationToken);
        probe.Metadata.StreamCount.Should().Be(2);
        probe.Metadata.Width.Should().Be(64);
        probe.Metadata.Height.Should().Be(48);
        probe.Metadata.VideoCodec.Should().Be("ffv1");
        probe.Metadata                .HasAudio.Should().BeTrue();
        probe.Metadata.FrameRate.Should().Be(5);
        probe.Metadata.DurationSeconds.Should().BeApproximately(8, 0.1);
        var hasher = new Sha256ContentHasher();
        var first = await hasher.HashAsync(path, revision, TestContext.Current.CancellationToken);
        var copy = TempFile("different-container-name.mp4", File.ReadAllBytes(path));
        (await hasher.HashAsync(copy, MediaFileAccess.Revision(copy), TestContext.Current.CancellationToken)).Sha256.Should().Be(first.Sha256);
        first.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public async Task Tool_license_and_config_are_LGPL_without_GPL_or_nonfree()
    {
        foreach (var tool in new[] { Probe, Ffmpeg })
        {
            var version = await Runner().RunAsync(new ProcessRequest(tool, ["-version"], TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
            version.ExitCode.Should().Be(0);
            version.StandardOutput.Should().NotContain("--enable-gpl").And.NotContain("--enable-nonfree");
            var license = await Runner().RunAsync(new ProcessRequest(tool, ["-L"], TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
            license.StandardOutput.Should().Contain("GNU Lesser General Public License").And.Contain("version 3");
        }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("{\"streams\":{}}")]
    [InlineData("{\"streams\":[")]
    public void Malformed_or_partial_metadata_is_typed(string json)
    {
        FluentActions.Invoking(() => FfprobeMediaProbe.Parse(json)).Should().Throw<MediaProcessingException>()
            .Which.Failure.Should().Be(MediaFailure.InvalidData);
    }

    [Theory]
    [InlineData("{\"streams\":[]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"pcm_s16le\"}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"unknown\"}]}")]
    public void No_video_or_unknown_codec_is_unsupported(string json)
    {
        FluentActions.Invoking(() => FfprobeMediaProbe.Parse(json)).Should().Throw<MediaProcessingException>()
            .Which.Failure.Should().Be(MediaFailure.Unsupported);
    }

    [Fact]
    public void Parses_multiple_streams_rotation_unknown_duration_and_bit_depth()
    {
        var metadata = FfprobeMediaProbe.Parse("""
            {"streams":[{"codec_type":"video","codec_name":"hevc","width":1920,"height":1080,
            "avg_frame_rate":"30000/1001","pix_fmt":"yuv420p10le","bits_per_raw_sample":"10",
            "color_space":"bt2020nc","side_data_list":[{"rotation":-90}]},
            {"codec_type":"audio","codec_name":"aac"},{"codec_type":"audio","codec_name":"opus"}],
            "format":{"duration":"N/A","format_name":"matroska,webm"}}
            """);
        metadata.StreamCount.Should().Be(3);
        metadata.Rotation.Should().Be(-90);
        metadata.BitDepth.Should().Be(10);
        metadata.FrameRate.Should().BeApproximately(29.97, 0.01);
        metadata.DurationSeconds.Should().BeNull();
        metadata.AudioCodec.Should().Be("aac");
    }

    [Fact]
    public async Task Probe_arguments_are_separate_and_file_changes_are_retryable()
    {
        var path = TempFile("-hostile & quote' unicode \u2603.mp4");
        var revision = MediaFileAccess.Revision(path);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<ProcessRequest>();
            request.Arguments.Last().Should().Be(path);
            request.Arguments.Should().ContainInOrder("-protocol_whitelist", "file");
            File.AppendAllText(path, "changed");
            return new ProcessResult(0, "{}", "");
        });
        await FluentActions.Awaiting(() => new FfprobeMediaProbe(runner, Probe).ProbeAsync(path, revision, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.Changed);
    }

    [Fact]
    public async Task Empty_corrupt_missing_locked_and_changed_files_have_typed_outcomes()
    {
        var empty = TempFile("empty.mp4", []);
        var hasher = new Sha256ContentHasher();
        (await hasher.HashAsync(empty, MediaFileAccess.Revision(empty), TestContext.Current.CancellationToken)).Sha256
            .Should().Be(Convert.ToHexStringLower(SHA256.HashData([])));
        await FluentActions.Awaiting(() => new FfprobeMediaProbe(Runner(), Probe).ProbeAsync(empty, MediaFileAccess.Revision(empty), TestContext.Current.CancellationToken))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.InvalidData);
        var corrupt = TempFile();
        await FluentActions.Awaiting(() => new FfprobeMediaProbe(Runner(), Probe).ProbeAsync(corrupt, MediaFileAccess.Revision(corrupt), TestContext.Current.CancellationToken))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.InvalidData);
        var revision = MediaFileAccess.Revision(corrupt);
        File.AppendAllText(corrupt, "change");
        await FluentActions.Awaiting(() => hasher.HashAsync(corrupt, revision, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.Changed);
        revision = MediaFileAccess.Revision(corrupt);
        using (var locked = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await FluentActions.Awaiting(() => hasher.HashAsync(corrupt, revision, TestContext.Current.CancellationToken))
                .Should().ThrowAsync<MediaProcessingException>();
        File.Delete(corrupt);
        await FluentActions.Awaiting(() => hasher.HashAsync(corrupt, revision, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.Missing);
    }

    [Fact]
    public async Task Runner_enforces_allowlist_start_failure_output_cap_timeout_and_cancellation()
    {
        await FluentActions.Awaiting(() => Runner().RunAsync(new ProcessRequest("unapproved", [], TimeSpan.FromSeconds(1))))
            .Should().ThrowAsync<ArgumentException>();
        var missing = Path.Combine(_temp, "missing.exe");
        await FluentActions.Awaiting(() => new ProcessRunner([missing]).RunAsync(new ProcessRequest(missing, [], TimeSpan.FromSeconds(1))))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.ToolUnavailable);
        await FluentActions.Awaiting(() => Runner().RunAsync(new ProcessRequest(Probe, ["-version"], TimeSpan.FromSeconds(5), 16)))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.OutputLimit);
        var infinite = new ProcessRequest(Ffmpeg, ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc=size=16x16:rate=1", "-f", "null", "-"], TimeSpan.FromMilliseconds(400));
        await FluentActions.Awaiting(() => Runner().RunAsync(infinite))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.Timeout);
        using var cancel = new CancellationTokenSource(200);
        await FluentActions.Awaiting(() => Runner().RunAsync(infinite with { Timeout = TimeSpan.FromSeconds(5) }, cancel.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        var failed = await Runner().RunAsync(new ProcessRequest(Probe, ["-invalid-option"], TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);
        failed.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task Large_file_hash_is_streamed_and_cancelable()
    {
        var path = TempFile();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(32 * 1024 * 1024);
        var result = await new Sha256ContentHasher().HashAsync(path, MediaFileAccess.Revision(path), TestContext.Current.CancellationToken);
        using var input = File.OpenRead(path);
        result.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(input)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await FluentActions.Awaiting(() => new Sha256ContentHasher().HashAsync(path, MediaFileAccess.Revision(path), cancel.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Queue_workers_persist_results_across_reopen_and_complete_jobs()
    {
        Directory.CreateDirectory(_temp);
        var db = Path.Combine(_temp, "index.db");
        using var queue = new SqliteJobQueue(db);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var repository = new SqliteMediaProcessingRepository(db);
        var path = TempFile("video.mkv", File.ReadAllBytes(Path.Combine(Root, "TestFixtures", "generated", "sample.mkv")));
        var item = new MediaWorkItem(Guid.NewGuid(), path, MediaFileAccess.Revision(path));
        foreach (var kind in new[] { JobType.Probe, JobType.Hash })
            await queue.EnqueueAsync(item.FileId, kind, MediaWorker.Payload(item), null, TestContext.Current.CancellationToken);
        await new ProbeWorker(queue, repository, new FfprobeMediaProbe(Runner(), Probe)).RunOnceAsync(TestContext.Current.CancellationToken);
        await new HashWorker(queue, repository, new Sha256ContentHasher()).RunOnceAsync(TestContext.Current.CancellationToken);
        var stored = await new SqliteMediaProcessingRepository(db).GetAsync(item.FileId, TestContext.Current.CancellationToken);
        stored.Should().NotBeNull();
        stored!.Metadata!.VideoCodec.Should().Be("ffv1");
        stored.Sha256.Should().HaveLength(64);
        (await queue.GetStatsAsync(TestContext.Current.CancellationToken)).Completed.Should().Be(2);
        // New revision cannot inherit an old revision's fingerprint or metadata.
        await repository.SaveAsync(new MediaProcessingRecord(item.FileId, item.Revision with { SizeBytes = 999 }, Failure: MediaFailure.Changed), TestContext.Current.CancellationToken);
        var stale = await repository.GetAsync(item.FileId, TestContext.Current.CancellationToken);
        stale!.Sha256.Should().BeNull(); stale.Metadata.Should().BeNull();
    }

    [Fact]
    public void Same_size_time_replacement_and_in_place_rewrite_invalidate_identity()
    {
        var path = TempFile();
        var before = MediaFileAccess.Revision(path);
        before.FileIdentity.Should().NotBeNull();

        // An in-place rewrite that keeps the size and restores the timestamp is witnessed through file ID
        // plus NTFS change time. Windows publishes that change time asynchronously (the dirty file record is
        // written back later), so the witness becomes observable only once the volume reports it: the guard
        // must reject the old revision exactly when a witness exists, and accept it while every component it
        // can observe - size, timestamp and witness - still matches.
        File.WriteAllBytes(path, [4, 5, 6]);
        File.SetLastWriteTimeUtc(path, before.ModifiedUtc.UtcDateTime);
        var rewritten = MediaFileAccess.Revision(path);
        rewritten.SizeBytes.Should().Be(before.SizeBytes);
        rewritten.ModifiedUtc.Should().Be(before.ModifiedUtc);
        if (rewritten.FileIdentity != before.FileIdentity)
            FluentActions.Invoking(() => MediaFileAccess.Verify(path, before)).Should().Throw<MediaProcessingException>();
        else
            MediaFileAccess.Verify(path, before);

        // A same-size, same-timestamp replacement is always witnessed, because the file ID changes.
        var replacement = TempFile("replacement.mp4", [7, 8, 9]);
        File.SetLastWriteTimeUtc(replacement, before.ModifiedUtc.UtcDateTime);
        File.Move(replacement, path, true);
        FluentActions.Invoking(() => MediaFileAccess.Verify(path, before)).Should().Throw<MediaProcessingException>();
    }

    [Fact]
    public void Any_single_revision_component_that_drifts_or_reports_a_witness_invalidates_it()
    {
        var path = TempFile();
        var current = MediaFileAccess.Revision(path);
        MediaFileAccess.Verify(path, current);
        FluentActions.Invoking(() => MediaFileAccess.Verify(path, current with { FileIdentity = current.FileIdentity + ":witness" })).Should().Throw<MediaProcessingException>();
        FluentActions.Invoking(() => MediaFileAccess.Verify(path, current with { SizeBytes = current.SizeBytes + 1 })).Should().Throw<MediaProcessingException>();
        FluentActions.Invoking(() => MediaFileAccess.Verify(path, current with { ModifiedUtc = current.ModifiedUtc.AddSeconds(1) })).Should().Throw<MediaProcessingException>();
    }

    [Fact]
    public async Task Inventory_queries_real_tool_and_rejects_absent_decoder()
    {
        var inventory = new DecoderInventory(Runner(), Probe);
        (await inventory.SupportsAsync("ffv1", TestContext.Current.CancellationToken)).Should().BeTrue();
        (await inventory.SupportsAsync("not_a_real_codec", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Discovery_enqueues_and_hosted_services_finish_then_rescan_skips_completed_work()
    {
        Directory.CreateDirectory(_temp);
        var input = Path.Combine(_temp, "input"); Directory.CreateDirectory(input);
        File.Copy(Path.Combine(Root, "TestFixtures", "generated", "sample.mkv"), Path.Combine(input, "sample.mkv"));
        using var queue = new SqliteJobQueue(Path.Combine(_temp, "hosted.db"));
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var repository = new SqliteMediaProcessingRepository(Path.Combine(_temp, "hosted.db"));
        var roots = new DirectoryScanRootRepository(Path.Combine(_temp, "roots"));
        var discovery = new DirectoryDiscoveryRepository(Path.Combine(_temp, "discovery"));
        var visuals = new SqliteVisualProcessingRepository(Path.Combine(_temp, "hosted.db"));
        var dashboard = new ProcessingDashboard(roots, new ScanOrchestrator(new FileSystemDiscoverySource(), discovery), discovery, repository, queue, new WorkFileAccess(), visuals);
        await dashboard.ScanAsync(input, TestContext.Current.CancellationToken);
        // Probe + Hash + VisualFingerprint enqueue only (no audio store), plus one deferred Match job
        // that the match worker skips while fingerprints are still outstanding.
        (await queue.GetStatsAsync(TestContext.Current.CancellationToken)).Queued.Should().Be(4);
        using var probe = new ProcessingService(new ProbeWorker(queue, repository, new FfprobeMediaProbe(Runner(), Probe, new DecoderInventory(Runner(), Probe))), Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingService>.Instance);
        using var hash = new ProcessingService(new HashWorker(queue, repository, new Sha256ContentHasher()), Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingService>.Instance);
        var mediaProbe = new FfprobeMediaProbe(Runner(), Probe);
        using var visual = new ProcessingService(new VisualFingerprintWorker(queue, repository, visuals, mediaProbe,
            new FfmpegVisualFingerprinter(Runner(), Ffmpeg)), Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingService>.Instance);
        await probe.StartAsync(TestContext.Current.CancellationToken); await hash.StartAsync(TestContext.Current.CancellationToken);
        await visual.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            while ((await queue.GetStatsAsync(timeout.Token)).Completed != 3) await Task.Delay(100, timeout.Token);
            var row = (await dashboard.RefreshAsync(timeout.Token)).Should().ContainSingle().Which;
            row.Status.Should().Be("Complete");
            row.Visual.Should().Be("1/1 usable");
            var storedVisual = await visuals.GetAsync((await discovery.GetFilesAsync((await roots.GetAllAsync(timeout.Token)).Single().Id, timeout.Token)).Single().Id, timeout.Token);
            storedVisual.Should().NotBeNull(); storedVisual!.Frames.Should().ContainSingle();
            await dashboard.ScanAsync(input, timeout.Token);
            // Probe/Hash/Visual completed; the Match job remains queued because the full pipeline is absent here.
            (await queue.GetStatsAsync(timeout.Token)).Should().Be(new QueueStats(1, 0, 3, 0));
            var changedSampling = new ProcessingDashboard(roots, new ScanOrchestrator(new FileSystemDiscoverySource(), discovery), discovery, repository, queue, new WorkFileAccess(), visuals,
                new VideoDedup.Domain.FrameSamplingPolicy(TimeSpan.FromSeconds(5)));
            await visual.StopAsync(timeout.Token);
            await changedSampling.ScanAsync(input, timeout.Token);
            var changedStats = await queue.GetStatsAsync(timeout.Token);
            changedStats.Queued.Should().Be(2, "the rescan adds a VisualFingerprint job for changed sampling; the deferred Match job is already queued");
            var rootId = (await roots.GetAllAsync(timeout.Token)).Single().Id;
            var visualJobs = await queue.ClaimAsync(new QueueClaimRequest("sampling-policy-test", TimeSpan.FromMinutes(1), AllowedJobTypes: [JobType.VisualFingerprint], DriveScope: [rootId.ToString("N")]), timeout.Token);
            visualJobs.Should().ContainSingle();
            visualJobs[0].PayloadJson.Should().Contain("sample.mkv");
        }
        finally
        {
            await probe.StopAsync(TestContext.Current.CancellationToken);
            await hash.StopAsync(TestContext.Current.CancellationToken);
            await visual.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
