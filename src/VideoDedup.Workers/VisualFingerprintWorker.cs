using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Workers;

public sealed class VisualFingerprintWorker(IJobQueue queue, IMediaProcessingRepository media, IVisualProcessingRepository visuals,
    IMediaProbe probe, IVisualFingerprinter fingerprinter, FrameSamplingPolicy? sampling = null) : MediaWorker(queue, JobType.VisualFingerprint)
{
    private readonly FrameSamplingPolicy _sampling = sampling ?? new();

    public override string Name => "Visual";

    protected override async Task ProcessAsync(MediaWorkItem item, CancellationToken cancellationToken)
    {
        var previous = await visuals.GetAsync(item.FileId, cancellationToken).ConfigureAwait(false);
        if (previous?.IsCurrent(item.Revision, _sampling) == true && previous.Failure is null) return;
        var stored = await media.GetAsync(item.FileId, cancellationToken).ConfigureAwait(false);
        var metadata = stored?.Revision == item.Revision ? stored.Metadata : null;
        metadata ??= (await probe.ProbeAsync(item.Path, item.Revision, cancellationToken).ConfigureAwait(false)).Metadata;
        var result = await fingerprinter.FingerprintAsync(item.Path, item.Revision, metadata, cancellationToken).ConfigureAwait(false);
        if (result.Revision != item.Revision) throw new MediaProcessingException(MediaFailure.Changed);
        await visuals.SaveAsync(Record(item, result.Frames), cancellationToken).ConfigureAwait(false);
    }
    protected override Task SaveFailureAsync(MediaWorkItem item, MediaFailure failure, CancellationToken cancellationToken) =>
        visuals.SaveAsync(Record(item, [], failure), cancellationToken);
    private VisualProcessingRecord Record(MediaWorkItem item, IReadOnlyList<VisualFingerprint> frames, MediaFailure? failure = null) =>
        new(item.FileId, item.Revision, VisualAlgorithm.Name, VisualAlgorithm.Version, _sampling.Interval.Ticks, _sampling.MaxFrames, frames, failure);
}
