using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public sealed class MatchingPolicyTests
{
    private static readonly IDuplicateMatchPolicy Policy = new DuplicateMatchPolicy(MatchPolicyOptions.Default);
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Equal_sha256_is_an_exact_match_even_without_perceptual_evidence()
    {
        var result = Policy.Evaluate(Candidate(shaA: HashA, shaB: HashA));
        result.MatchType.Should().Be(MatchType.Exact);
        result.OverallScore.Should().Be(1m);
        result.Reasons.Should().Contain(reason => reason.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public void Different_sha256_does_not_prevent_a_near_duplicate_decision()
    {
        var result = Policy.Evaluate(Candidate(shaA: HashA, shaB: HashB, visual: 0.96m, audio: 0.94m));
        result.MatchType.Should().Be(MatchType.NearDuplicate);
        result.OverallScore.Should().BeGreaterThanOrEqualTo(MatchPolicyOptions.Default.NearDuplicateThreshold);
        result.Reasons.Should().Contain(reason => reason.Contains("visual", StringComparison.OrdinalIgnoreCase));
        result.Reasons.Should().Contain(reason => reason.Contains("audio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void One_strong_fingerprint_signal_can_create_a_near_duplicate_candidate()
    {
        var result = Policy.Evaluate(Candidate(visual: 0.93m));
        result.MatchType.Should().Be(MatchType.NearDuplicate);
        result.Reasons.Should().ContainSingle();
    }

    [Fact]
    public void Borderline_similarity_is_manual_review_not_an_automatic_match()
    {
        var result = Policy.Evaluate(Candidate(visual: 0.75m));
        result.MatchType.Should().Be(MatchType.NeedsReview);
        result.Reasons.Should().NotBeEmpty();
    }

    [Fact]
    public void Strongly_conflicting_visual_and_audio_evidence_requires_review()
    {
        var result = Policy.Evaluate(Candidate(visual: 0.99m, audio: 0.05m));
        result.MatchType.Should().Be(MatchType.NeedsReview);
        result.Reasons.Should().Contain(reason => reason.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_fingerprint_evidence_is_not_a_match()
    {
        var result = Policy.Evaluate(Candidate());
        result.MatchType.Should().Be(MatchType.Different);
        result.OverallScore.Should().Be(0m);
        result.Reasons.Should().Contain(reason => reason.Contains("No fingerprint", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Candidate_rejects_similarity_outside_zero_to_one(double score) =>
        FluentActions.Invoking(() => Candidate(visual: (decimal)score)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Candidate_rejects_empty_file_ids_and_null_candidate_policy_input()
    {
        FluentActions.Invoking(() => MatchCandidate.Create(Guid.Empty, FileB)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Policy.Evaluate(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Candidate_rejects_malformed_hash_and_same_file_pair()
    {
        FluentActions.Invoking(() => Candidate(shaA: "not-a-sha256")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => MatchCandidate.Create(FileA, FileA)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_require_valid_ordered_thresholds_and_weights()
    {
        FluentActions.Invoking(() => new MatchPolicyOptions(1.1m, 0.5m, 0.6m, 0.4m, 0.6m))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new MatchPolicyOptions(0.9m, 0.8m, 0.6m, 0.6m, 0.4m))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decisions_are_deterministic_and_scores_stay_within_unit_interval()
    {
        var candidate = Candidate(visual: 0.82m, audio: 0.71m);
        var first = Policy.Evaluate(candidate);
        first.Should().BeEquivalentTo(Policy.Evaluate(candidate));
        first.OverallScore.Should().BeInRange(0m, 1m);
    }

    [Fact]
    public void Fingerprint_value_objects_validate_bounds_versions_and_copy_audio_payload()
    {
        var version = FingerprintVersion.Create(1, 0);
        VisualFingerprint.Create(TimeSpan.FromSeconds(1), 0x1234UL, 0.8m, version)
            .Timestamp.Should().Be(TimeSpan.FromSeconds(1));

        var bytes = new byte[] { 1, 2 };
        var audio = AudioFingerprint.Create(TimeSpan.Zero, TimeSpan.FromSeconds(3), bytes, version);
        bytes[0] = 9;
        audio.Data.Should().Equal(new byte[] { 1, 2 });
        var returned = audio.Data;
        returned[1] = 8;
        audio.Data.Should().Equal(new byte[] { 1, 2 });

        FluentActions.Invoking(() => FingerprintVersion.Create(0, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => FingerprintVersion.Create(1, -1)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => VisualFingerprint.Create(TimeSpan.FromSeconds(-1), 0UL, 0.5m, version))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => VisualFingerprint.Create(TimeSpan.Zero, 0UL, 1.1m, version))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => AudioFingerprint.Create(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), new byte[] { 1 }, version))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AudioFingerprint.Create(TimeSpan.Zero, TimeSpan.FromSeconds(1), Array.Empty<byte>(), version))
            .Should().Throw<ArgumentException>();
    }

    private static MatchCandidate Candidate(string? shaA = null, string? shaB = null, decimal? visual = null, decimal? audio = null) =>
        MatchCandidate.Create(FileA, FileB, shaA, shaB, visual, audio);

    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
}
