using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>
/// Read-only join over every store that makes up the index, for the export document. It only reads: no source file
/// is opened, moved, or deleted here, and the quarantine log is read through the same port the review UI uses.
/// </summary>
public sealed class AppIndexSnapshotSource(
    IScanRootRepository roots,
    IDiscoveryRepository discovery,
    IMediaProcessingRepository results,
    IVisualProcessingRepository visuals,
    IAudioProcessingRepository audios,
    IMatchResultRepository matches,
    IReviewDecisionStore decisions,
    IQuarantineLog quarantine) : IAppIndexSnapshotSource
{
    public async Task<IReadOnlyList<string>> GetScanRootPathsAsync(CancellationToken cancellationToken)
    {
        var configured = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return configured.Select(root => root.Path).ToArray();
    }

    public async Task<AppIndexSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var configured = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var files = new List<IndexedFile>();
        foreach (var root in configured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.AddRange(await discovery.GetFilesAsync(root.Id, cancellationToken).ConfigureAwait(false));
        }

        files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        var processing = new List<MediaProcessingRecord>();
        var visual = new List<VisualProcessingRecord>();
        var audio = new List<AudioProcessingRecord>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await results.GetAsync(file.Id, cancellationToken).ConfigureAwait(false) is { } result)
            {
                processing.Add(result);
            }

            if (await visuals.GetAsync(file.Id, cancellationToken).ConfigureAwait(false) is { } frames)
            {
                visual.Add(frames);
            }

            if (await audios.GetAsync(file.Id, cancellationToken).ConfigureAwait(false) is { } fingerprint)
            {
                audio.Add(fingerprint);
            }
        }

        return new AppIndexSnapshot(configured, files, processing, visual, audio,
            await matches.GetAllAsync(cancellationToken).ConfigureAwait(false),
            await decisions.GetAllAsync(cancellationToken).ConfigureAwait(false),
            await quarantine.GetAsync(cancellationToken).ConfigureAwait(false));
    }
}
