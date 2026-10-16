using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public sealed class QuarantineSafetyTests
{
    private static QuarantineSafetyFacts Facts(
        bool exists = true,
        bool link = false,
        bool insideRoot = true,
        bool rootAvailable = true,
        bool rootEnabled = true,
        bool hasHash = true,
        int otherCopies = 1) =>
        new(exists, link, insideRoot, rootAvailable, rootEnabled, hasHash, otherCopies);

    [Fact]
    public void A_reachable_copy_with_another_indexed_copy_may_be_quarantined()
    {
        var verdict = QuarantineSafetyPolicy.Evaluate(Facts(otherCopies: 2));

        verdict.IsAllowed.Should().BeTrue();
        verdict.BlockedBy.Should().BeNull();
        verdict.Reasons.Should().ContainSingle().Which.Should().Contain("2 other");
    }

    [Theory]
    [InlineData(false, false, true, true, true, 1, QuarantineBlockReason.FileMissing)]
    [InlineData(true, true, true, true, true, 1, QuarantineBlockReason.SymbolicLink)]
    [InlineData(true, false, false, true, true, 1, QuarantineBlockReason.PathOutsideRoot)]
    [InlineData(true, false, true, false, true, 1, QuarantineBlockReason.RootUnavailable)]
    [InlineData(true, false, true, true, false, 1, QuarantineBlockReason.RootDisabled)]
    [InlineData(true, false, true, true, true, 0, QuarantineBlockReason.LastRemainingCopy)]
    public void Unsafe_facts_block_quarantining_with_a_reason(bool exists, bool link, bool insideRoot, bool available,
        bool enabled, int copies, QuarantineBlockReason expected)
    {
        var verdict = QuarantineSafetyPolicy.Evaluate(Facts(exists, link, insideRoot, available, enabled, otherCopies: copies));

        verdict.IsAllowed.Should().BeFalse();
        verdict.BlockedBy.Should().Be(expected);
        verdict.Reasons.Should().NotBeEmpty();
    }

    [Fact]
    public void A_missing_file_is_reported_before_the_missing_copy_guard_so_offline_drives_read_correctly()
    {
        var verdict = QuarantineSafetyPolicy.Evaluate(Facts(exists: false, otherCopies: 0));

        verdict.BlockedBy.Should().Be(QuarantineBlockReason.FileMissing);
    }

    [Fact]
    public void An_unknown_content_hash_cannot_prove_another_copy_exists()
    {
        var verdict = QuarantineSafetyPolicy.Evaluate(Facts(hasHash: false, otherCopies: 0));

        verdict.BlockedBy.Should().Be(QuarantineBlockReason.LastRemainingCopy);
        verdict.Reasons.Should().Contain(reason => reason.Contains("no content hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Negative_copy_counts_are_rejected()
    {
        FluentActions.Invoking(() => QuarantineSafetyPolicy.Evaluate(Facts(otherCopies: -1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Blocked_verdicts_must_explain_themselves_and_reject_control_characters()
    {
        FluentActions.Invoking(() => QuarantineSafetyVerdict.Blocked(QuarantineBlockReason.FileMissing, Array.Empty<string>()))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => QuarantineSafetyVerdict.Blocked(QuarantineBlockReason.FileMissing, ["\u0001bad"]))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => QuarantineSafetyVerdict.Blocked((QuarantineBlockReason)99, ["why"]))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}

public sealed class QuarantineDestinationTests
{
    [Fact]
    public void A_volume_level_quarantine_folder_is_chosen_when_it_sits_outside_every_root()
    {
        var destination = QuarantineDestinationPolicy.Choose(@"D:\Videos\clips\a.mkv", @"D:\",
            [@"D:\Videos"], @"C:\Users\me\AppData\Local\VideoDedup\Quarantine", "VOL1");

        destination.SameVolume.Should().BeTrue();
        destination.Directory.Should().Be("D:/VideoDedup Quarantine");
    }

    [Fact]
    public void The_per_user_store_is_used_when_the_same_volume_folder_would_be_inside_a_root()
    {
        var destination = QuarantineDestinationPolicy.Choose(@"D:\Videos\clips\a.mkv", @"D:\",
            [@"D:\"], @"C:\Users\me\AppData\Local\VideoDedup\Quarantine", "VOL1");

        destination.SameVolume.Should().BeFalse();
        destination.Directory.Should().Be("C:/Users/me/AppData/Local/VideoDedup/Quarantine/VOL1");
        destination.Reason.Should().Contain("per-user quarantine store");
    }

    [Fact]
    public void A_root_beneath_the_candidate_also_forces_the_per_user_store()
    {
        var destination = QuarantineDestinationPolicy.Choose(@"D:\Videos\clips\a.mkv", @"D:\",
            [@"D:\VideoDedup Quarantine\deep"], @"C:\q", "VOL1");

        destination.SameVolume.Should().BeFalse();
    }

    [Fact]
    public void A_source_already_inside_the_candidate_never_selects_it_again()
    {
        var destination = QuarantineDestinationPolicy.Choose(@"D:\VideoDedup Quarantine\a.mkv", @"D:\",
            [@"D:\Videos"], @"C:\q", "VOL1");

        destination.SameVolume.Should().BeFalse();
        destination.Directory.Should().Be("C:/q/VOL1");
    }

    [Fact]
    public void Missing_same_volume_base_and_relative_paths_fall_back_or_throw()
    {
        var destination = QuarantineDestinationPolicy.Choose(@"D:\Videos\a.mkv", sameVolumeBasePath: null,
            [], @"C:\q", "VOL1");
        destination.SameVolume.Should().BeFalse();

        FluentActions.Invoking(() => QuarantineDestinationPolicy.Choose(@"D:\Videos\a.mkv", @"D:\", [], "relative", "VOL1"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => QuarantineDestinationPolicy.Choose("relative/a.mkv", null, [], @"C:\q", "VOL1"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => QuarantineDestinationPolicy.Choose(@"D:\a.mkv", null, [], @"C:\q", " "))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Path_containment_respects_directory_boundaries_and_case()
    {
        QuarantineDestinationPolicy.IsWithin(@"C:\Videos\a.mkv", @"C:\Videos").Should().BeTrue();
        QuarantineDestinationPolicy.IsWithin(@"C:\Videos", @"c:\videos").Should().BeTrue();
        QuarantineDestinationPolicy.IsWithin(@"C:\Videos\a.mkv", @"C:\Videos\").Should().BeTrue();
        QuarantineDestinationPolicy.IsWithin(@"C:\Videos2\a.mkv", @"C:\Videos").Should().BeFalse();
        QuarantineDestinationPolicy.IsWithin(@"C:\Other\a.mkv", @"C:\Videos").Should().BeFalse();
    }
}

public sealed class KeepRecommendationTests
{
    private static readonly Guid Higher = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Lower = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static KeepCandidateFacts Candidate(Guid id, long size, int? width, int? height,
        int dayOffset = 0, bool available = true) =>
        new(id, available, size, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(dayOffset), width, height, TimeSpan.FromMinutes(1));

    [Fact]
    public void The_richest_encode_is_recommended()
    {
        var recommendation = KeepRecommendationPolicy.Recommend(
            [Candidate(Lower, 900_000_000, 1280, 720), Candidate(Higher, 100_000_000, 3840, 2160)]);

        recommendation.FileId.Should().Be(Higher);
        recommendation.HasSuggestion.Should().BeTrue();
        recommendation.Reason.Should().Contain("3840x2160");
    }

    [Fact]
    public void Unavailable_copies_are_never_recommended()
    {
        var recommendation = KeepRecommendationPolicy.Recommend(
            [Candidate(Higher, 100, 3840, 2160, available: false), Candidate(Lower, 200, 1280, 720)]);

        recommendation.FileId.Should().Be(Lower);
    }

    [Fact]
    public void Resolution_is_unknown_for_both_copies_so_the_larger_payload_wins()
    {
        var recommendation = KeepRecommendationPolicy.Recommend(
            [Candidate(Lower, 300, null, null), Candidate(Higher, 900, null, null)]);

        recommendation.FileId.Should().Be(Higher);
        recommendation.Reason.Should().Contain("resolution is unknown");
    }

    [Fact]
    public void Ties_break_by_newest_modification_then_file_id_deterministically()
    {
        var newest = KeepRecommendationPolicy.Recommend(
            [Candidate(Lower, 100, 1920, 1080, dayOffset: 1), Candidate(Higher, 100, 1920, 1080, dayOffset: 9)]);
        newest.FileId.Should().Be(Higher);

        var identical = KeepRecommendationPolicy.Recommend(
            [Candidate(Lower, 100, 1920, 1080), Candidate(Higher, 100, 1920, 1080)]);
        identical.FileId.Should().Be(Higher);
        KeepRecommendationPolicy.Recommend(
            [Candidate(Higher, 100, 1920, 1080), Candidate(Lower, 100, 1920, 1080)])
            .FileId.Should().Be(identical.FileId);
    }

    [Fact]
    public void No_reachable_copy_produces_an_explicit_empty_suggestion()
    {
        var recommendation = KeepRecommendationPolicy.Recommend(
            [Candidate(Higher, 10, 640, 480, available: false), Candidate(Lower, 10, 640, 480, available: false)]);

        recommendation.HasSuggestion.Should().BeFalse();
        recommendation.FileId.Should().BeNull();
        recommendation.Reason.Should().Contain("nothing can be recommended");
    }

    [Fact]
    public void Null_candidate_sources_are_rejected()
    {
        FluentActions.Invoking(() => KeepRecommendationPolicy.Recommend(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
