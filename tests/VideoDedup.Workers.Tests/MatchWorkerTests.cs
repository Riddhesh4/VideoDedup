using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Workers;

namespace VideoDedup.Workers.Tests;

public sealed class MatchWorkerTests
{
    private readonly IJobQueue _queue = Substitute.For<IJobQueue>();
    private readonly IMatchOrchestrator _orchestrator = Substitute.For<IMatchOrchestrator>();
    private readonly MediaWorkItem _item = new(Guid.NewGuid(), Path.GetFullPath("video ☃.mkv"), new(100, DateTimeOffset.UnixEpoch));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public MatchWorkerTests()
    {
        var job = new QueueJob(Guid.NewGuid(), _item.FileId, JobType.Match, JobState.Leased, 0, 0, 5,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test", DateTimeOffset.MaxValue, MediaWorker.Payload(_item), "root", null);
        _queue.ClaimAsync(Arg.Any<QueueClaimRequest>(), Arg.Any<CancellationToken>()).Returns([job]);
        _orchestrator.GetReadinessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(MatchReadiness.Ready);
    }

    private MatchWorker Worker() => new(_queue, _orchestrator);

    [Fact]
    public async Task Matching_delegates_to_the_orchestrator_and_completes_the_job()
    {
        await Worker().RunOnceAsync(Token);
        await _orchestrator.Received(1).MatchFileAsync(_item.FileId, Token);
        await _queue.Received(1).CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_still_being_fingerprinted_is_retried_instead_of_matched_or_completed()
    {
        // This is the measured first-scan case: Match jobs are enqueued with the fingerprint jobs, so a job claimed
        // early must come back rather than report a file as matched when nothing was compared.
        _orchestrator.GetReadinessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(MatchReadiness.InProgress);
        await Worker().RunOnceAsync(Token);

        await _orchestrator.DidNotReceive().MatchFileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _queue.Received(1).FailAsync(Arg.Any<Guid>(), Arg.Any<string>(),
            MediaFailure.EvidenceNotReady.ToString(), Arg.Any<CancellationToken>());
        await _queue.DidNotReceive().CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_that_can_never_be_processed_is_completed_without_retrying_forever()
    {
        _orchestrator.GetReadinessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(MatchReadiness.Unprocessable);
        await Worker().RunOnceAsync(Token);

        await _orchestrator.DidNotReceive().MatchFileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _queue.Received(1).CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Storage_failure_is_nonfatal_for_the_file_and_retries_via_queue_policy()
    {
        // Match failures have no per-file media state (unlike fingerprints), so no failure row is written.
        _orchestrator.MatchFileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<MatchPersistenceRecord>>(
                new MediaProcessingException(MediaFailure.InvalidData)));
        await Worker().RunOnceAsync(Token);
        await _queue.Received(1).CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _orchestrator.DidNotReceive().MatchAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_leaves_the_job_leased_for_recovery()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _orchestrator.MatchFileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancel.Cancel();
            return Task.FromCanceled<IReadOnlyList<MatchPersistenceRecord>>(cancel.Token);
        });
        await FluentActions.Awaiting(() => Worker().RunOnceAsync(cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default, default!, Arg.Any<CancellationToken>());
    }
}
