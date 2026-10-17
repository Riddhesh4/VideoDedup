using VideoDedup.Application;

namespace VideoDedup.Workers;

/// <summary>
/// Bridges durable Match jobs into the orchestrator. A claimed job matches one file against every other
/// fully-processed file. Persisted decisions accumulate; a job encountering transient storage errors is
/// retried by the queue policy, and cancellation leaves the lease for recovery like other workers.
/// </summary>
public sealed class MatchWorker(IJobQueue queue, IMatchOrchestrator orchestrator) : MediaWorker(queue, JobType.Match)
{
    public override string Name => "Match";

    protected override async Task ProcessAsync(MediaWorkItem item, CancellationToken cancellationToken)
    {
        // Measured on a real scan: Match jobs are enqueued together with the fingerprint jobs, so a claimed Match job
        // usually arrives before that file's evidence exists. Completing it there is how a folder of identical copies
        // reported zero duplicates, so an unfinished file is reported as retryable instead (the queue backs off and
        // brings the job back) and only a file that can never be processed is completed without matching.
        var readiness = await orchestrator.GetReadinessAsync(item.FileId, cancellationToken).ConfigureAwait(false);
        if (readiness is MatchReadiness.InProgress)
        {
            throw new MediaProcessingException(MediaFailure.EvidenceNotReady);
        }

        if (readiness is MatchReadiness.Unprocessable or MatchReadiness.NotIndexed)
        {
            return;
        }

        await orchestrator.MatchFileAsync(item.FileId, cancellationToken).ConfigureAwait(false);
    }

    protected override Task SaveFailureAsync(MediaWorkItem item, MediaFailure failure, CancellationToken cancellationToken) =>
        // Matching failures carry no per-file media state to persist; the queue's retry/backoff policy
        // drives recovery and the next scan enqueues fresh Match jobs if evidence changed meanwhile.
        Task.CompletedTask;
}
