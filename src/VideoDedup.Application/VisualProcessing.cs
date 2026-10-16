using VideoDedup.Domain;

namespace VideoDedup.Application;

public sealed record VisualFingerprintResult(FileRevision Revision, IReadOnlyList<VisualFingerprint> Frames);
public interface IVisualFingerprinter
{
    Task<VisualFingerprintResult> FingerprintAsync(string path, FileRevision expected, MediaMetadata metadata, CancellationToken cancellationToken);
}
public sealed record VisualProcessingRecord(Guid FileId, FileRevision Revision, string Algorithm, FingerprintVersion Version,
    long IntervalTicks, int MaxFrames, IReadOnlyList<VisualFingerprint> Frames, MediaFailure? Failure = null)
{
    public bool IsCurrent(FileRevision revision, FrameSamplingPolicy sampling) => Revision == revision &&
        Algorithm == VisualAlgorithm.Name && Version == VisualAlgorithm.Version && IntervalTicks == sampling.Interval.Ticks && MaxFrames == sampling.MaxFrames;
}
public interface IVisualProcessingRepository
{
    Task SaveAsync(VisualProcessingRecord record, CancellationToken cancellationToken);
    Task<VisualProcessingRecord?> GetAsync(Guid fileId, CancellationToken cancellationToken);
}
