using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public sealed class PartialClipClassificationTests
{
    private static readonly DuplicateMatchPolicy Policy = new(MatchPolicyOptions.Default);
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static MatchCandidate Candidate(string? sha256 = null, decimal? partialAudio = null, decimal? offset = null, decimal? visualAtOffset = null,
        decimal? visual = null, decimal? audio = null, FingerprintVersion? visualVersion = null, FingerprintVersion? audioVersion = null) =>
        MatchCandidate.Create(FileA, FileB, sha256A: sha256, sha256B: sha256, visualVersion: visualVersion, audioVersion: audioVersion,
            visualSimilarity: visual, audioSimilarity: audio,
            visualSimilarityAtOffset: visualAtOffset,
            partialAudioSimilarity: partialAudio, partialAudioOffsetSeconds: offset);

    [Fact]
    public void Strong_partial_audio_alignment_with_visual_confirmation_is_a_partial_clip()
    {
        var result = Policy.Evaluate(Candidate(partialAudio: 0.95m, offset: 8m, visualAtOffset: 0.85m));
        result.MatchType.Should().Be(MatchType.PartialClip);
        result.OverallScore.Should().BeInRange(0m, 1m);
        result.AudioOffsetSeconds.Should().Be(8m);
        result.Reasons.Should().Contain(reason => reason.Contains("8", StringComparison.Ordinal), "the detected offset must be explained");
        result.Reasons.Should().Contain(reason => reason.Contains("visual", StringComparison.OrdinalIgnoreCase), "confirmation evidence is cited");
    }

    [Fact]
    public void Strong_partial_alignment_without_visual_confirmation_is_manual_review_only()
    {
        var result = Policy.Evaluate(Candidate(partialAudio: 0.95m, offset: 4m, visualAtOffset: 0.5m));
        result.MatchType.Should().Be(MatchType.NeedsReview);
        result.Reasons.Should().Contain(reason => reason.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_visual_confirmation_stays_review_not_partial()
    {
        Policy.Evaluate(Candidate(partialAudio: 0.95m, offset: 4m)).MatchType.Should().Be(MatchType.NeedsReview);
    }

    [Fact]
    public void Weak_partial_alignment_falls_through_to_whole_file_classification()
    {
        Policy.Evaluate(Candidate(partialAudio: 0.75m, offset: 12m, visualAtOffset: 0.4m)).MatchType.Should().Be(MatchType.Different);
        Policy.Evaluate(Candidate(partialAudio: 0.4m, offset: 12m, visualAtOffset: 0.1m)).MatchType.Should().Be(MatchType.Different);
    }

    [Fact]
    public void Exact_hash_equality_outranks_partial_alignment()
    {
        var result = Policy.Evaluate(Candidate(sha256: HashA, partialAudio: 0.95m, offset: 8m, visualAtOffset: 0.9m));
        result.MatchType.Should().Be(MatchType.Exact);
    }

    [Fact]
    public void Stale_fingerprint_versions_are_not_used_as_evidence()
    {
        var stale = Policy.Evaluate(Candidate(visual: 0.99m, visualVersion: FingerprintVersion.Create(1, 9)));
        stale.MatchType.Should().Be(MatchType.Different, "stale visual fingerprints must not accumulate evidence");
        stale.Reasons.Should().Contain(reason => reason.Contains("stale", StringComparison.OrdinalIgnoreCase));

        stale = Policy.Evaluate(Candidate(audio: 0.99m, audioVersion: FingerprintVersion.Create(1, 9)));
        stale.MatchType.Should().Be(MatchType.Different, "stale audio fingerprints must not accumulate evidence");
        stale.Reasons.Should().Contain(reason => reason.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Current_versions_are_cited_in_the_decision_components()
    {
        var result = Policy.Evaluate(Candidate(visual: 0.93m, visualVersion: VisualAlgorithm.Version, audioVersion: AudioAlgorithm.Version));
        result.MatchType.Should().Be(MatchType.NearDuplicate);
        result.PolicyVersion.Should().NotBeNull();
        result.EvidenceVersions.VisualVersion.Should().Be(VisualAlgorithm.Version);
        result.EvidenceVersions.AudioVersion.Should().Be(AudioAlgorithm.Version);
        result.VisualSimilarityComponent.Should().Be(0.93m);
        result.AudioSimilarityComponent.Should().BeNull();
    }

    [Fact]
    public void Partial_clip_score_averages_audio_alignment_and_visual_confirmation()
    {
        var result = Policy.Evaluate(Candidate(partialAudio: 0.9m, offset: 2m, visualAtOffset: 0.8m));
        result.OverallScore.Should().Be(0.85m);
    }
}
