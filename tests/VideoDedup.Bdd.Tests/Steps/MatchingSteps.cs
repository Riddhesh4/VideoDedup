using VideoDedup.Application;
using VideoDedup.Domain;
using Reqnroll;
using MatchTypeDomain = VideoDedup.Domain.MatchType;

namespace VideoDedup.Bdd.Tests.Steps;

[Binding]
public sealed class MatchingSteps
{
    private MatchResult _decision = null!;
    private RecordingMatchRepository _repository = null!;
    private IReadOnlyList<MatchPersistenceRecord> _orchestratedRows = Array.Empty<MatchPersistenceRecord>();

    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid PendingFile = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private (decimal Offset, decimal Alignment) _excerpt;

    private static DuplicateMatchPolicy Policy() => new(MatchPolicyOptions.Default);

    private static MatchCandidate Candidate(string? shaA = null, string? shaB = null, decimal? visual = null, decimal? audio = null,
        decimal? partialAudio = null, decimal? offset = null, decimal? visualAtOffset = null) =>
        MatchCandidate.Create(FileA, FileB, shaA, shaB,
            visualVersion: visual is null ? null : VisualAlgorithm.Version,
            audioVersion: (audio is not null || partialAudio is not null) ? AudioAlgorithm.Version : null,
            visualSimilarity: visual, audioSimilarity: audio,
            visualSimilarityAtOffset: visualAtOffset,
            partialAudioSimilarity: partialAudio, partialAudioOffsetSeconds: offset);

    private static AudioFingerprint Audio(TimeSpan duration, IReadOnlyList<uint> words) =>
        AudioFingerprint.Create(TimeSpan.Zero, duration, words.SelectMany(BitConverter.GetBytes).ToArray(), AudioAlgorithm.Version);

    private static List<uint> Words(int count, uint seed) =>
        Enumerable.Range(0, count).Select(i => seed + (uint)i * 0x9E3779B1u).ToList();

    private static IReadOnlyList<VisualFingerprint> FrameSequence(int count, int hashSeed) =>
        Enumerable.Range(0, count).Select(index => VisualFingerprint.Create(
            TimeSpan.FromSeconds(index), (ulong)(hashSeed + index) * 0x0404040404040401UL, 0.8m, VisualAlgorithm.Version)).ToList();

    private static IMatchEvidenceSource EvidenceSource(FileMatchEvidence[] entries) => new StaticEvidenceSource(entries);

    private static (Guid RootOne, Guid RootTwo) Roots() => (
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Given(@"two indexed files share the same SHA-256 hash")]
    public void GivenIdenticalHashes() =>
        _decision = Policy().Evaluate(Candidate(shaA: ShaA, shaB: ShaA));

    [Given(@"two versions score (.*)% on aligned visual frames and (.*)% on audio words")]
    public void GivenStrongSignalPair(int visualPercent, int audioPercent) =>
        _decision = Policy().Evaluate(Candidate(visual: visualPercent / 100m, audio: audioPercent / 100m));

    [Given(@"audio of one file sits at offset (.*) seconds inside the other with (.*)% alignment")]
    public void GivenAlignedExcerpt(decimal offsetSeconds, int alignmentPercent) =>
        _excerpt = (offsetSeconds, alignmentPercent / 100m);

    [Given(@"visual content at that offset confirms with (.*)% similarity")]
    public void GivenVisualConfirmation(int percent)
    {
        _decision = Policy().Evaluate(Candidate(shaA: ShaA, shaB: ShaB,
            partialAudio: _excerpt.Alignment, offset: _excerpt.Offset, visualAtOffset: percent / 100m));
    }

    [Given(@"visual content at the offset does not confirm")]
    public void GivenNoVisualConfirmation()
    {
        _decision = Policy().Evaluate(Candidate(shaA: ShaA, shaB: ShaB,
            partialAudio: _excerpt.Alignment, offset: _excerpt.Offset, visualAtOffset: 0.4m));
    }

    [When(@"the pair is classified")]
    public void WhenClassified()
    {
        // The decision was produced in the Given steps; this trigger exists only for scenario readability.
    }

    [Then(@"the decision is an exact duplicate with maximum confidence")]
    public void ThenExactDuplicate()
    {
        _decision.MatchType.Should().Be(MatchTypeDomain.Exact);
        _decision.OverallScore.Should().Be(1m);
        _decision.Reasons.Should().Contain(reason => reason.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Then(@"the decision is a near-duplicate citing both signals")]
    public void ThenNearDuplicate()
    {
        _decision.MatchType.Should().Be(MatchTypeDomain.NearDuplicate);
        _decision.Reasons.Should().Contain(reason => reason.Contains("Visual", StringComparison.OrdinalIgnoreCase));
        _decision.Reasons.Should().Contain(reason => reason.Contains("Audio", StringComparison.OrdinalIgnoreCase));
    }

    [Then(@"the decision is a partial clip citing the offset and the confirmation")]
    public void ThenPartialClip()
    {
        _decision.MatchType.Should().Be(MatchTypeDomain.PartialClip);
        _decision.AudioOffsetSeconds.Should().Be(8m);
        _decision.Reasons.Should().Contain(reason => reason.Contains("offset", StringComparison.OrdinalIgnoreCase));
        _decision.Reasons.Should().Contain(reason => reason.Contains("confirm", StringComparison.OrdinalIgnoreCase));
    }

    [Then(@"the decision is manual review citing the unconfirmed alignment")]
    public void ThenManualReview()
    {
        _decision.MatchType.Should().Be(MatchTypeDomain.NeedsReview);
        _decision.Reasons.Should().Contain(reason => reason.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase));
    }

    [Then(@"the pair is not classified as a partial clip automatically")]
    public void ThenNotPartialClip() => _decision.MatchType.Should().NotBe(MatchTypeDomain.PartialClip);

    [Given(@"one fully processed file on drive one and a matching copy on drive two")]
    public void GivenCrossDriveCopies()
    {
        var (rootOne, rootTwo) = Roots();
        var audio = Audio(TimeSpan.FromSeconds(64), Words(32, 1u));
        var frames = FrameSequence(6, 1);
        _orchestratedRows = new MatchOrchestrator(EvidenceSource(new[]
        {
            new FileMatchEvidence(FileA, rootOne, ShaA, frames, audio, IsReady: true),
            new FileMatchEvidence(FileB, rootTwo, ShaA, frames, audio, IsReady: true),
        }), Repository(), Policy()).MatchAllAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private IMatchResultRepository Repository() => _repository = new RecordingMatchRepository();

    [When(@"the match orchestrator runs")]
    public void WhenOrchestratorRuns()
    {
        var (rootOne, rootTwo) = Roots();
        var audio = Audio(TimeSpan.FromSeconds(64), Words(32, 1u));
        var frames = FrameSequence(6, 1);
        _orchestratedRows = new MatchOrchestrator(EvidenceSource(new[]
        {
            new FileMatchEvidence(FileA, rootOne, ShaA, frames, audio, IsReady: true),
            new FileMatchEvidence(PendingFile, rootOne, ShaB, Array.Empty<VisualFingerprint>(), null, IsReady: false),
            new FileMatchEvidence(FileB, rootTwo, ShaA, frames, audio, IsReady: true),
        }), Repository(), Policy()).MatchAllAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Then(@"the cross-drive pair is evaluated and persisted")]
    public void ThenCrossDrivePairPersisted()
    {
        _orchestratedRows.Should().ContainSingle();
        var row = _orchestratedRows.Single();
        row.MatchType.Should().Be(MatchTypeDomain.Exact);
        { row.FileIdA.Should().Be(FileA); row.FileIdB.Should().Be(FileB); }
        _repository.Saves.Should().HaveCount(1);
    }

    [Then(@"files that are not fully processed are skipped")]
    public void ThenUnreadySkipped() =>
        _orchestratedRows.Should().OnlyContain(row => row.FileIdA != PendingFile && row.FileIdB != PendingFile);
}

file sealed class StaticEvidenceSource(params FileMatchEvidence[] entries) : IMatchEvidenceSource
{
    private readonly IReadOnlyList<FileMatchEvidence> _entries = entries;

    public Task<IReadOnlyList<FileMatchEvidence>> GetEvidenceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_entries);
}

internal sealed class RecordingMatchRepository : IMatchResultRepository
{
    public List<MatchPersistenceRecord> Saves { get; } = new();

    public Task SaveAsync(MatchPersistenceRecord record, CancellationToken cancellationToken)
    {
        Saves.Add(record);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid fileIdA, Guid fileIdB, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<IReadOnlyList<MatchPersistenceRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MatchPersistenceRecord> snapshot = Saves.ToArray();
        return Task.FromResult(snapshot);
    }
}
