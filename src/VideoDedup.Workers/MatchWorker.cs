using VideoDedup.Application;

namespace VideoDedup.Workers;

/// <summary>
/// Bridges durable Match jobs into the orchestrator. A claimed job matches one file against every other
/// fully-processed file. Persisted decisions accumulate; a job encountering transient storage errors is
/// retried by the queue policy, and cancellation leaves the lease for recovery like other workers.
/// </summary>
public sealed class MatchWorker(IJobQueue queue, IMatchOrchestrator orchestrator) : MediaWorker(queue, JobType.Match)
{
    protected override async Task ProcessAsync(MediaWorkItem item, CancellationToken cancellationToken)
    {
        await orchestrator.MatchFileAsync(item.FileId, cancellationToken).ConfigureAwait(false);
    }

    protected override Task SaveFailureAsync(MediaWorkItem item, MediaFailure failure, CancellationToken cancellationToken) =>
        // Matching failures carry no per-file media state to persist; the queue's retry/backoff policy
        // drives recovery and the next scan enqueues fresh Match jobs if evidence changed meanwhile.
        Task.CompletedTask;
}
