using System.Text.Json;
using VideoDedup.Application;

namespace VideoDedup.Workers;

/// <summary>Host calls RunOnceAsync off the UI thread; one bounded job per invocation, with no DB transaction across processing.</summary>
public abstract class MediaWorker(IJobQueue queue, JobType jobType)
{
    private readonly string _owner = $"{jobType}-{Guid.NewGuid():N}";

    /// <summary>Stable display name used by pause/resume and the per-worker dashboard.</summary>
    public abstract string Name { get; }

    protected abstract Task ProcessAsync(MediaWorkItem item, CancellationToken cancellationToken);
    protected abstract Task SaveFailureAsync(MediaWorkItem item, MediaFailure failure, CancellationToken cancellationToken);

    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var jobs = await queue.ClaimAsync(new QueueClaimRequest(_owner, TimeSpan.FromMinutes(10),
            AllowedJobTypes: [jobType], MaxPerDrive: 1, MaxGlobal: 4), cancellationToken).ConfigureAwait(false);
        if (jobs.Count == 0) return false;
        var job = jobs[0];
        MediaWorkItem? item = null;
        try
        {
            QueuePayloadValidator.Validate(job.PayloadJson);
            using var doc = JsonDocument.Parse(job.PayloadJson);
            item = doc.RootElement.GetProperty("work").Deserialize<MediaWorkItem>();
            if (item is null || item.Revision is null || item.FileId != job.FileId || !Path.IsPathFullyQualified(item.Path) || item.Revision.SizeBytes < 0)
                throw new MediaProcessingException(MediaFailure.InvalidData);
            await ProcessAsync(item, cancellationToken).ConfigureAwait(false);
            await queue.CompleteAsync(job.Id, _owner, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is MediaProcessingException or JsonException or KeyNotFoundException or ArgumentException)
        {
            var failure = exception is MediaProcessingException media ? media : new MediaProcessingException(MediaFailure.InvalidData);
            if (item is not null && item.Revision is not null && item.FileId == job.FileId)
                await SaveFailureAsync(item, failure.Failure, cancellationToken).ConfigureAwait(false);
            if (failure.Retryable) await queue.FailAsync(job.Id, _owner, failure.Failure.ToString(), cancellationToken).ConfigureAwait(false);
            else await queue.CompleteAsync(job.Id, _owner, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public static string Payload(MediaWorkItem item) => JsonSerializer.Serialize(new { version = 1, work = item });
}

public sealed class ProbeWorker(IJobQueue queue, IMediaProcessingRepository repository, IMediaProbe probe)
    : MediaWorker(queue, JobType.Probe)
{
    public override string Name => "Probe";

    protected override async Task ProcessAsync(MediaWorkItem item, CancellationToken cancellationToken)
    {
        var result = await probe.ProbeAsync(item.Path, item.Revision, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(new MediaProcessingRecord(item.FileId, result.Revision, Metadata: result.Metadata), cancellationToken).ConfigureAwait(false);
    }
    protected override Task SaveFailureAsync(MediaWorkItem item, MediaFailure failure, CancellationToken cancellationToken) =>
        repository.SaveAsync(new MediaProcessingRecord(item.FileId, item.Revision, Failure: failure), cancellationToken);
}

public sealed class HashWorker(IJobQueue queue, IMediaProcessingRepository repository, IContentHasher hasher)
    : MediaWorker(queue, JobType.Hash)
{
    public override string Name => "Hash";

    protected override async Task ProcessAsync(MediaWorkItem item, CancellationToken cancellationToken)
    {
        var result = await hasher.HashAsync(item.Path, item.Revision, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(new MediaProcessingRecord(item.FileId, result.Revision, Sha256: result.Sha256), cancellationToken).ConfigureAwait(false);
    }
    protected override Task SaveFailureAsync(MediaWorkItem item, MediaFailure failure, CancellationToken cancellationToken) =>
        repository.SaveAsync(new MediaProcessingRecord(item.FileId, item.Revision, Failure: failure), cancellationToken);
}
