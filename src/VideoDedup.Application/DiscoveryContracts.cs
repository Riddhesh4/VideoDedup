using VideoDedup.Domain;

namespace VideoDedup.Application;

public sealed class DiscoveryRootUnavailableException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public sealed record FileObservation(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    bool IsHidden = false,
    bool IsSystem = false,
    bool IsSymbolicLink = false);

public sealed record IndexedFile(
    Guid Id,
    Guid ScanRootId,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    ScanState ScanState,
    TimeSpan? Duration = null,
    int? Width = null,
    int? Height = null,
    string? VideoCodec = null,
    string? AudioCodec = null,
    bool? HasAudio = null,
    string? Sha256 = null);

public interface IFileDiscoverySource
{
    Task<DriveDetails> GetDriveDetailsAsync(ScanRoot root, CancellationToken cancellationToken);

    IAsyncEnumerable<FileObservation> EnumerateFilesAsync(ScanRoot root, CancellationToken cancellationToken);
}

public interface IDiscoveryRepository
{
    Task<IReadOnlyList<IndexedFile>> GetFilesAsync(Guid scanRootId, CancellationToken cancellationToken);

    Task<string?> GetVolumeSignatureAsync(Guid scanRootId, CancellationToken cancellationToken);

    Task SaveFileAsync(IndexedFile file, CancellationToken cancellationToken);

    Task SetRootStatusAsync(Guid scanRootId, string? volumeSignature, RootAvailability availability, CancellationToken cancellationToken);
}

public enum RootAvailability
{
    Available,
    Unavailable,
}

public enum DiscoveryOutcome
{
    Completed,
    RootUnavailable,
    Disabled,
    NetworkShareNotScanned,
}

public sealed record DiscoveryResult(
    Guid ScanRootId,
    DiscoveryOutcome Outcome,
    int EnumeratedCount,
    int AddedCount,
    int ChangedCount,
    int UnchangedCount,
    int NeedsReviewCount,
    int SkippedCount,
    IReadOnlyList<string> Warnings);
