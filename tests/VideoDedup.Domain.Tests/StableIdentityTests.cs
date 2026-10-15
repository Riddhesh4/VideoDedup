using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public sealed class StableIdentityTests
{
    [Fact]
    public void Create_rejects_empty_or_traversing_relative_paths()
    {
        var signature = "volume-123";
        var modified = DateTimeOffset.Parse("2025-01-02T03:04:05Z");

        FluentActions.Invoking(() => StableFileIdentity.Create(signature, " ", 12, modified))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => StableFileIdentity.Create(signature, "../escape.mp4", 12, modified))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => StableFileIdentity.Create(signature, @"folder\..\escape.mp4", 12, modified))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => StableFileIdentity.Create(signature, @"C:\Movies\clip.mp4", 12, modified))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_null_relative_path_and_normalizes_path_separators()
    {
        var modified = DateTimeOffset.Parse("2025-01-02T03:04:05Z");
        FluentActions.Invoking(() => StableFileIdentity.Create("vol", null!, 12, modified))
            .Should().Throw<ArgumentNullException>();

        var identity = StableFileIdentity.Create(" vol ", "folder/clip.mp4", 12, modified);
        identity.VolumeSignature.Should().Be("vol");
        identity.RelativePath.Should().Be("folder/clip.mp4");
        identity.SizeBytes.Should().Be(12);
    }

    [Fact]
    public void Create_rejects_negative_size()
    {
        var modified = DateTimeOffset.Parse("2025-01-02T03:04:05Z");
        FluentActions.Invoking(() => StableFileIdentity.Create("vol", "clip.mp4", -1, modified))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CompareTo_confirms_same_volume_path_and_file_revision()
    {
        var a = Identity("volume-A", "movies/clip.mp4", 100, "2025-01-02T03:04:05Z");
        var b = Identity("volume-A", "Movies/CLIP.mp4", 100, "2025-01-02T03:04:05Z");

        a.CompareTo(b).Should().Be(StableIdentityMatch.ConfirmedSame);
    }

    [Fact]
    public void CompareTo_marks_same_path_with_changed_volume_signature_for_review()
    {
        var a = Identity("volume-A", "movies/clip.mp4", 100, "2025-01-02T03:04:05Z");
        var b = Identity("volume-B", "movies/clip.mp4", 100, "2025-01-02T03:04:05Z");

        a.CompareTo(b).Should().Be(StableIdentityMatch.NeedsReview);
    }

    [Fact]
    public void CompareTo_marks_possible_rename_or_missing_signature_for_review()
    {
        var a = Identity("volume-A", "movies/clip.mp4", 100, "2025-01-02T03:04:05Z");
        var renamed = Identity("volume-A", "archive/renamed.mp4", 100, "2025-01-02T03:04:05Z");
        var unknown = Identity(null, "movies/clip.mp4", 100, "2025-01-02T03:04:05Z");

        a.CompareTo(renamed).Should().Be(StableIdentityMatch.NeedsReview);
        a.CompareTo(unknown).Should().Be(StableIdentityMatch.NeedsReview);
    }

    [Fact]
    public void Create_rejects_invalid_signature_control_characters()
    {
        FluentActions.Invoking(() => StableFileIdentity.Create("vol\u0001bad", "clip.mp4", 1, DateTimeOffset.UnixEpoch))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CompareTo_reports_different_when_revision_metadata_changes()
    {
        var a = Identity("volume-A", "movies/clip.mp4", 100, "2025-01-02T03:04:05Z");
        var changed = Identity("volume-A", "movies/clip.mp4", 101, "2025-01-02T03:04:05Z");

        a.CompareTo(changed).Should().Be(StableIdentityMatch.Different);
    }

    private static StableFileIdentity Identity(string? volume, string relativePath, long size, string modified) =>
        StableFileIdentity.Create(volume, relativePath, size, DateTimeOffset.Parse(modified));
}
