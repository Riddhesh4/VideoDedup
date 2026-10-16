using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Workers;

/// <summary>
/// Hosts one worker loop. Pausing stops new claims immediately; a job already leased finishes or is recovered by
/// lease expiry, so pausing never abandons half-processed work. Failures are reported as sanitized type/kind text
/// so a message can never leak a full local path into logs or the dashboard.
/// </summary>
public sealed class ProcessingService(MediaWorker worker, IWorkerControl control, ILogger<ProcessingService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan PauseDelay = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (control.IsPaused)
                {
                    await Task.Delay(PauseDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                control.ReportRunning(worker.Name);
                var processed = await Task.Run(() => worker.RunOnceAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
                control.ReportIdle(worker.Name, processed);
                if (!processed)
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var error = Describe(exception);
                control.ReportFailure(worker.Name, error);
                logger.LogWarning("Processing iteration failed ({ErrorType}); lease recovery retains unfinished work.", error);
                await Task.Delay(FailureDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        control.ReportStopped(worker.Name);
    }

    private static string Describe(Exception exception) => exception is MediaProcessingException media
        ? $"{media.GetType().Name}: {media.Failure}"
        : exception.GetType().Name;
}

public sealed class ProcessingDashboard(IScanRootRepository roots, IScanOrchestrator scanner,
    IDiscoveryRepository discovery, IMediaProcessingRepository results, IJobQueue queue, IWorkFileAccess access,
    IVisualProcessingRepository? visuals = null, FrameSamplingPolicy? sampling = null, IAudioProcessingRepository? audios = null,
    IWorkerControl? control = null) : IProcessingDashboard
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
            if (audios is not null)
            {
                var audio = await audios.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                if (audio?.IsCurrent(work.Revision) != true ||
                    (audio.Failure is MediaFailure failure && new MediaProcessingException(failure).Retryable))
                    await queue.EnqueueAsync(file.Id, JobType.AudioFingerprint, MediaWorker.Payload(work), new EnqueueOptions(DriveId: root.Id.ToString("N")), cancellationToken).ConfigureAwait(false);
            }
            // Matching runs last: the match worker skips files whose fingerprints are not yet current,
            // so enqueueing here is harmless while fingerprint work still queues.
            await queue.EnqueueAsync(file.Id, JobType.Match, MediaWorker.Payload(work), new EnqueueOptions(DriveId: root.Id.ToString("N")), cancellationToken).ConfigureAwait(false);
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
                var audio = audios is null ? null : await audios.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                var audioCurrent = result is not null && audio?.IsCurrent(result.Revision) == true;
                var audioStatus = audios is null ? "Not enabled" : !audioCurrent ? "Pending" : audio!.Failure?.ToString() ?? (!audio.HasAudio ? "No audio" : "Fingerprint ready");
                rows.Add(new ProcessingRow(file.RelativePath, result?.Failure?.ToString() ??
                    (result?.Metadata is not null && result.Sha256 is not null && (visuals is null || visualCurrent) && (audios is null || audioCurrent) ? "Complete" : "Pending"),
                    result?.Sha256, result?.Metadata?.VideoCodec, visualStatus, audioStatus));
            }
        return rows;
    }
    public Task<QueueStats> StatsAsync(CancellationToken cancellationToken) => queue.GetStatsAsync(cancellationToken);

    public IReadOnlyList<WorkerStatus> WorkerStates() => control?.Snapshot() ?? Array.Empty<WorkerStatus>();

    public void PauseWorkers() => control?.Pause();

    public void ResumeWorkers() => control?.Resume();
}
