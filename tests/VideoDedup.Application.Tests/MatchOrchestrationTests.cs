using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Application.Tests;

public sealed class MatchOrchestrationTests
{
    private static readonly Guid IdA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid IdB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid IdC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Root1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Root2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly IMatchResultRepository _repository = Substitute.For<IMatchResultRepository>();

    private MatchOrchestrator Orchestrator(params FileMatchEvidence[] evidence) => new(
        new StaticEvidenceSource(evidence),
        _repository,
        new DuplicateMatchPolicy(MatchPolicyOptions.Default));

    private static FileMatchEvidence Ready(Guid fileId, Guid root, string? sha = null,
        AudioFingerprint? audio = null, IReadOnlyList<VisualFingerprint>? frames = null) => new(
        fileId, root, sha, frames ?? Array.Empty<VisualFingerprint>(), audio, IsReady: true);

    private static AudioFingerprint Audio(TimeSpan duration, IReadOnlyList<uint> words) =>
        AudioFingerprint.Create(TimeSpan.Zero, duration,
            words.SelectMany(BitConverter.GetBytes).ToArray(), AudioAlgorithm.Version);

    private static List<uint> Words(int count, uint seed) =>
        Enumerable.Range(0, count).Select(i => seed + (uint)i * 0x9E3779B1u).ToList();

    [Fact]
    public async Task Readiness_separates_still_arriving_evidence_from_a_file_that_is_not_indexed()
    {
        var orchestrator = Orchestrator(
            new FileMatchEvidence(IdA, Root1, ShaA, Array.Empty<VisualFingerprint>(), null, IsReady: false),
            Ready(IdB, Root2, ShaA));
        var token = TestContext.Current.CancellationToken;

        (await orchestrator.GetReadinessAsync(IdA, token)).Should().Be(MatchReadiness.InProgress);
        (await orchestrator.GetReadinessAsync(IdB, token)).Should().Be(MatchReadiness.Ready);
        (await orchestrator.GetReadinessAsync(Guid.NewGuid(), token)).Should().Be(MatchReadiness.NotIndexed);
        await FluentActions.Awaiting(() => orchestrator.GetReadinessAsync(Guid.Empty, token))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_single_file_job_whose_fingerprints_are_not_written_yet_is_retryable_never_silently_done()
    {
        // Measured on a first scan: Match jobs are claimed while the fingerprint workers are still running. Completing
        // such a job is how a scan of twelve identical copies reported zero matches.
        var orchestrator = Orchestrator(
            new FileMatchEvidence(IdA, Root1, ShaA, Array.Empty<VisualFingerprint>(), null, IsReady: false),
            Ready(IdB, Root2, ShaA));

        var failure = await FluentActions
            .Awaiting(() => orchestrator.MatchFileAsync(IdA, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<MediaProcessingException>();

        failure.Which.Failure.Should().Be(MediaFailure.EvidenceNotReady);
        failure.Which.Retryable.Should().BeTrue("the queue must bring the job back once the fingerprints exist");
        await _repository.DidNotReceive().SaveAsync(Arg.Any<MatchPersistenceRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_whole_index_pass_still_skips_files_that_are_not_ready_instead_of_failing()
    {
        var orchestrator = Orchestrator(
            new FileMatchEvidence(IdA, Root1, ShaA, Array.Empty<VisualFingerprint>(), null, IsReady: false),
            Ready(IdB, Root2, ShaA));

        (await orchestrator.MatchAllAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public void Evidence_not_ready_is_the_only_new_retryable_failure_and_terminal_failures_stay_terminal()
    {
        new MediaProcessingException(MediaFailure.EvidenceNotReady).Retryable.Should().BeTrue();
        new MediaProcessingException(MediaFailure.Unsupported).Retryable.Should().BeFalse();
        new MediaProcessingException(MediaFailure.InvalidData).Retryable.Should().BeFalse();
        new MediaProcessingException(MediaFailure.Missing).Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Exact_hash_pairs_are_persisted_as_exact()
    {
        var evidence = Orchestrator(
            Ready(IdA, Root1, ShaA),
            Ready(IdB, Root2, ShaA));
        var records = await evidence.MatchAllAsync(TestContext.Current.CancellationToken);

        await _repository.Received(1).SaveAsync(
            Arg.Is<MatchPersistenceRecord>(record => record.MatchType == MatchType.Exact && record.OverallScore == 1m),
            Arg.Any<CancellationToken>());
        records.Should().ContainSingle();
    }

    [Fact]
    public async Task Near_duplicate_pairs_persist_with_component_scores_and_versions()
    {
        var frames = Enumerable.Range(0, 6).Select(index => VisualFingerprint.Create(
            TimeSpan.FromSeconds(index), (ulong)(index + 1) * 0x0101010101010101UL, 0.8m, VisualAlgorithm.Version)).ToList();
        var audio = Audio(TimeSpan.FromSeconds(64), Words(32, 1u));
        var evidence = Orchestrator(
            Ready(IdA, Root1, ShaA, audio, frames),
            Ready(IdB, Root2, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", audio, frames));

        var records = await evidence.MatchAllAsync(TestContext.Current.CancellationToken);
        records.Should().ContainSingle();
        var record = records[0];
        record.MatchType.Should().Be(MatchType.NearDuplicate);
        record.PolicyVersion.Should().Be(MatchPolicyOptions.PolicyVersion);
        record.EvidenceVersions.AudioVersion.Should().Be(AudioAlgorithm.Version);
        record.EvidenceVersions.VisualVersion.Should().Be(VisualAlgorithm.Version);
        record.Reasons.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Excerpt_of_a_longer_audio_track_is_a_partial_clip_candidate_with_offset()
    {
        var fullAudio = Audio(TimeSpan.FromSeconds(32), Words(32, 0xBADC0DE0u));
        var excerptAudio = Audio(TimeSpan.FromSeconds(8), Words(32, 0xBADC0DE0u).Skip(8).Take(8).ToList());
        // Container video has frames across its whole 0..13 s timeline; the excerpt covers 8..13 s,
        // so the excerpt's own local frames (0..5 s hashed as container 8..13) confirm at the offset.
        ulong ContainerHash(int index) => (ulong)(index + 1) * 0x0101010101010101UL;
        var containerFrames = Enumerable.Range(0, 14).Select(index => VisualFingerprint.Create(
            TimeSpan.FromSeconds(index), ContainerHash(index), 0.8m, VisualAlgorithm.Version)).ToList();
        var excerptFrames = Enumerable.Range(0, 6).Select(index => VisualFingerprint.Create(
            TimeSpan.FromSeconds(index), ContainerHash(index + 8), 0.8m, VisualAlgorithm.Version)).ToList();
        var evidence = Orchestrator(
            Ready(IdA, Root1, ShaA, fullAudio, containerFrames),
            Ready(IdB, Root1, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", excerptAudio, excerptFrames));

        var records = await evidence.MatchAllAsync(TestContext.Current.CancellationToken);
        records.Should().ContainSingle();
        var record = records[0];
        record.MatchType.Should().Be(MatchType.PartialClip);
        record.AudioOffsetSeconds.Should().BeGreaterThan(6.9m);
        record.AudioOffsetSeconds.Should().BeLessThanOrEqualTo(8.5m);
    }

    [Fact]
    public async Task Different_files_are_not_persisted()
    {
        var framesA = Enumerable.Range(0, 6).Select(index => VisualFingerprint.Create(
            TimeSpan.FromSeconds(index), (ulong)(index + 1) * 0x1111111111111111UL, 0.8m, VisualAlgorithm.Version)).ToList();
        var framesB = Enumerable.Range(0, 6).Select(index => VisualFingerprint.Create(
            TimeSpan.FromSeconds(index), (ulong)(index + 1) * 0x5555555555555555UL, 0.8m, VisualAlgorithm.Version)).ToList();
        var evidence = Orchestrator(
            Ready(IdA, Root1, ShaA, Audio(TimeSpan.FromSeconds(64), Words(32, 1u)), framesA),
            Ready(IdB, Root2, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                Audio(TimeSpan.FromSeconds(64), Words(32, 0xF00D0000u)), framesB));

        await evidence.MatchAllAsync(TestContext.Current.CancellationToken);
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Files_that_are_not_fully_processed_are_skipped()
    {
        var notReady = new FileMatchEvidence(IdA, Root1, ShaA, Array.Empty<VisualFingerprint>(), null, IsReady: false);
        var evidence = new MatchOrchestrator(
            new StaticEvidenceSource(notReady, Ready(IdB, Root2, ShaA)),
            _repository, new DuplicateMatchPolicy(MatchPolicyOptions.Default));

        var records = await evidence.MatchAllAsync(TestContext.Current.CancellationToken);
        records.Should().BeEmpty();
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Single_file_matches_only_against_the_requested_file()
    {
        var evidence = new MatchOrchestrator(
            new StaticEvidenceSource(Ready(IdA, Root1, ShaA), Ready(IdB, Root2, ShaA), Ready(IdC, Root2, ShaA)),
            _repository, new DuplicateMatchPolicy(MatchPolicyOptions.Default));

        var records = await evidence.MatchFileAsync(IdB, TestContext.Current.CancellationToken);
        await _repository.Received(2).SaveAsync(Arg.Any<MatchPersistenceRecord>(), Arg.Any<CancellationToken>());
        records.Should().HaveCount(2);
        records.Select(record => record.OtherPairId(IdB)).Should().OnlyContain(id => id != IdB);
    }
}

file sealed class StaticEvidenceSource(params FileMatchEvidence[] entries) : IMatchEvidenceSource
{
    private readonly IReadOnlyList<FileMatchEvidence> _entries = entries;

    public Task<IReadOnlyList<FileMatchEvidence>> GetEvidenceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_entries);

    public Task<MatchReadiness> GetReadinessAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var entry = _entries.FirstOrDefault(candidate => candidate.FileId == fileId);
        return Task.FromResult(entry is null
            ? MatchReadiness.NotIndexed
            : entry.IsReady ? MatchReadiness.Ready : MatchReadiness.InProgress);
    }
}

file static class PairExtensions
{
    public static Guid OtherPairId(this MatchPersistenceRecord record, Guid self) =>
        record.FileIdA == self ? record.FileIdB : record.FileIdA;
}
