namespace VideoDedup.Application;

public enum MediaFailure { Missing, AccessDenied, Changed, Unsupported, InvalidData, ToolUnavailable, ToolFailed, Timeout, OutputLimit }

public sealed class MediaProcessingException(MediaFailure failure) : Exception($"Media processing failed: {failure}.")
{
    public MediaFailure Failure { get; } = failure;
    public bool Retryable => Failure is MediaFailure.Changed or MediaFailure.AccessDenied or MediaFailure.Timeout or MediaFailure.ToolUnavailable or MediaFailure.ToolFailed;
}

public sealed record FileRevision(long SizeBytes, DateTimeOffset ModifiedUtc, string? FileIdentity = null);
public sealed record MediaMetadata(double? DurationSeconds, int StreamCount, int? Width, int? Height,
    string? VideoCodec, string? AudioCodec, bool HasAudio, double? FrameRate, string? Container,
    double? Rotation, string? PixelFormat, string? ColorSpace, int? BitDepth);
public sealed record ProbeResult(FileRevision Revision, MediaMetadata Metadata);
public sealed record HashResult(FileRevision Revision, string Sha256);
public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string path, FileRevision expected, CancellationToken cancellationToken);
}
public interface IContentHasher
{
    Task<HashResult> HashAsync(string path, FileRevision expected, CancellationToken cancellationToken);
}

public sealed record MediaWorkItem(Guid FileId, string Path, FileRevision Revision);
public sealed record MediaProcessingRecord(Guid FileId, FileRevision Revision, MediaMetadata? Metadata = null,
    string? Sha256 = null, MediaFailure? Failure = null);
public interface IMediaProcessingRepository
{
    Task SaveAsync(MediaProcessingRecord record, CancellationToken cancellationToken);
    Task<MediaProcessingRecord?> GetAsync(Guid fileId, CancellationToken cancellationToken);
}

public sealed record ProcessingRow(string Path, string Status, string? Sha256, string? Codec, string? Visual = null, string? Audio = null);
public interface IProcessingDashboard
{
    Task ScanAsync(string folder, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProcessingRow>> RefreshAsync(CancellationToken cancellationToken);
    Task<QueueStats> StatsAsync(CancellationToken cancellationToken);
}

public interface IWorkFileAccess
{
    MediaWorkItem Capture(Guid fileId, string path);
}
