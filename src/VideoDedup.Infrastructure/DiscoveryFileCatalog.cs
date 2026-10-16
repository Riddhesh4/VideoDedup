using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

/// <summary>
/// Read-only join of configured roots, indexed files, and probed metadata, used by the review and quarantine flows.
/// Rows are returned defensively: a file whose media record is missing still appears with the discovery values.
/// </summary>
public sealed class DiscoveryFileCatalog(IScanRootRepository roots, IDiscoveryRepository discovery,
    IMediaProcessingRepository? media = null) : IIndexedFileCatalog
{
    public async Task<IReadOnlyList<IndexedFileEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<IndexedFileEntry>();
        foreach (var root in await roots.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var file in await discovery.GetFilesAsync(root.Id, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = media is null ? null : await media.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                var metadata = result?.Metadata;
                entries.Add(new IndexedFileEntry(file.Id, root.Id, root.Label, root.Path, root.Enabled, file.RelativePath,
                    file.SizeBytes, file.ModifiedUtc, result?.Sha256 ?? file.Sha256,
                    Duration(metadata?.DurationSeconds, file.Duration),
                    metadata?.Width ?? file.Width, metadata?.Height ?? file.Height,
                    metadata?.VideoCodec ?? file.VideoCodec, metadata?.AudioCodec ?? file.AudioCodec,
                    metadata?.HasAudio ?? file.HasAudio, metadata?.Container));
            }
        }

        return entries;
    }

    private static TimeSpan? Duration(double? seconds, TimeSpan? fallback)
    {
        if (seconds is not double value || !double.IsFinite(value) || value < 0 || value > FrameSamplingPolicy.DurationLimit.TotalSeconds)
        {
            return fallback;
        }

        return TimeSpan.FromSeconds(value);
    }
}
