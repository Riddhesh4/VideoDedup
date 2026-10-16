using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Workers;

public sealed class ProcessingService(MediaWorker worker, ILogger<ProcessingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await Task.Run(() => worker.RunOnceAsync(stoppingToken), stoppingToken).ConfigureAwait(false))
                    await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Processing iteration failed ({ErrorType}); lease recovery retains unfinished work.", exception.GetType().Name);
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

public sealed class ProcessingDashboard(IScanRootRepository roots, IScanOrchestrator scanner,
    IDiscoveryRepository discovery, IMediaProcessingRepository results, IJobQueue queue, IWorkFileAccess access,
    IVisualProcessingRepository? visuals = null, FrameSamplingPolicy? sampling = null) : IProcessingDashboard
{
    public async Task ScanAsync(string folder, CancellationToken cancellationToken)
    {
        var configured = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var root = configured.FirstOrDefault(r => string.Equals(r.Path.Replace('\\', '/').TrimEnd('/'), folder.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            ?? ScanRoot.Create(Guid.NewGuid(), "Videos", folder, ScanRootType.LocalFolder);
        await roots.SaveAsync(root, cancellationToken).ConfigureAwait(false);
        var outcome = await scanner.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        if (outcome.Outcome != DiscoveryOutcome.Completed || outcome.NeedsReviewCount != 0) return;
        foreach (var file in await discovery.GetFilesAsync(root.Id, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaWorkItem work;
            try { work = access.Capture(file.Id, Path.Combine(root.Path, file.RelativePath)); }
            catch (MediaProcessingException) { continue; }
            var previous = await results.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
            if (previous?.Revision != work.Revision || previous.Failure is not null || previous.Metadata is null || previous.Sha256 is null)
                foreach (var type in new[] { JobType.Probe, JobType.Hash })
                    await queue.EnqueueAsync(file.Id, type, MediaWorker.Payload(work), new EnqueueOptions(DriveId: root.Id.ToString("N")), cancellationToken).ConfigureAwait(false);
            if (visuals is not null)
            {
                var visual = await visuals.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                if (visual?.IsCurrent(work.Revision, sampling ?? new()) != true ||
                    (visual.Failure is MediaFailure failure && new MediaProcessingException(failure).Retryable))
                    await queue.EnqueueAsync(file.Id, JobType.VisualFingerprint, MediaWorker.Payload(work), new EnqueueOptions(DriveId: root.Id.ToString("N")), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<ProcessingRow>> RefreshAsync(CancellationToken cancellationToken)
    {
        var rows = new List<ProcessingRow>();
        foreach (var root in await roots.GetAllAsync(cancellationToken).ConfigureAwait(false))
            foreach (var file in await discovery.GetFilesAsync(root.Id, cancellationToken).ConfigureAwait(false))
            {
                var result = await results.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                var visual = visuals is null ? null : await visuals.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                var visualCurrent = result is not null && visual?.IsCurrent(result.Revision, sampling ?? new()) == true;
                var visualStatus = visuals is null ? "Not enabled" : !visualCurrent ? "Pending" :
                    visual!.Failure?.ToString() ?? $"{visual.Frames.Count(f => f.Disposition == FrameDisposition.Usable)}/{visual.Frames.Count} usable";
                rows.Add(new ProcessingRow(file.RelativePath, result?.Failure?.ToString() ??
                    (result?.Metadata is not null && result.Sha256 is not null && (visuals is null || visualCurrent) ? "Complete" : "Pending"),
                    result?.Sha256, result?.Metadata?.VideoCodec, visualStatus));
            }
        return rows;
    }
    public Task<QueueStats> StatsAsync(CancellationToken cancellationToken) => queue.GetStatsAsync(cancellationToken);
}
