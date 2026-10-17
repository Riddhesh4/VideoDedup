using VideoDedup.Domain;

namespace VideoDedup.Application;

/// <summary>Coarse areas of application-owned data shown in the privacy and data panel.</summary>
public enum AppDataArea
{
    IndexDatabase,
    DiscoverySnapshots,
    Configuration,
    ProcessingLogs,
    QuarantineStore,
    Unrecognized,
}

/// <summary>Usage of one area. Reclaimable counts only what the retention policy allows to be deleted.</summary>
public sealed record AppDataAreaUsage(
    AppDataArea Area,
    long SizeBytes,
    int FileCount,
    long ReclaimableBytes,
    int ReclaimableFileCount);

/// <summary>What the application owns on this machine, and what deleting the index would actually remove.</summary>
public sealed record MaintenanceReport(
    string DataRoot,
    IReadOnlyList<AppDataAreaUsage> Areas,
    long TotalBytes,
    long ReclaimableBytes,
    int ReclaimableFileCount,
    int QuarantineFileCount,
    long QuarantineBytes,
    IReadOnlyList<string> Notes)
{
    public bool HasReclaimableData => ReclaimableFileCount > 0;
}

/// <summary>Read-only view of everything the application indexed, assembled for export.</summary>
public sealed record AppIndexSnapshot(
    IReadOnlyList<ScanRoot> Roots,
    IReadOnlyList<IndexedFile> Files,
    IReadOnlyList<MediaProcessingRecord> Processing,
    IReadOnlyList<VisualProcessingRecord> Visual,
    IReadOnlyList<AudioProcessingRecord> Audio,
    IReadOnlyList<MatchPersistenceRecord> Matches,
    IReadOnlyList<ReviewDecisionRecord> Decisions,
    IReadOnlyList<QuarantineRecord> Quarantine);

/// <summary>
/// Gathers the index for export. Implementations only read; nothing here can modify source media.
/// </summary>
public interface IAppIndexSnapshotSource
{
    /// <summary>Scan-root paths, used by the retention policy to refuse deleting inside a scanned folder.</summary>
    Task<IReadOnlyList<string>> GetScanRootPathsAsync(CancellationToken cancellationToken);

    Task<AppIndexSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>Export request. <paramref name="IncludePaths"/> is the user's explicit privacy choice.</summary>
public sealed record IndexExportRequest(string DestinationPath, bool IncludePaths);

public sealed record IndexExportResult(
    string DestinationPath,
    long SizeBytes,
    bool IncludedPaths,
    int RootCount,
    int FileCount,
    int MatchCount,
    int QuarantineRecordCount);

/// <summary>
/// Result of a delete-index request. Nothing is removed unless <paramref name="Confirmed"/> is true, and retained
/// entries are always reported with the reason, so the panel can never quietly keep data the user asked to remove.
/// </summary>
public sealed record IndexDeletionResult(
    bool Confirmed,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<AppDataDecision> Retained,
    IReadOnlyList<string> Failures)
{
    public static IndexDeletionResult NotConfirmed => new(false, [], [], []);

    public bool Succeeded => Confirmed && Failures.Count == 0;
}

/// <summary>
/// Filesystem surface the maintenance service needs. It is deliberately small: enumerate, measure, delete a file,
/// delete empty folders, and write an export. No source-media operation is expressible through it.
/// </summary>
public interface IAppDataStore
{
    /// <summary>Absolute path of the per-user application data folder.</summary>
    string DataRoot { get; }

    /// <summary>Every file under the data folder, classified and measured. Reparse points are reported, never followed.</summary>
    IReadOnlyList<AppDataEntry> Enumerate();

    void DeleteFile(string path);

    /// <summary>Removes folders under the data folder that are empty, deepest first. The data folder itself stays.</summary>
    void DeleteEmptyDirectories();

    /// <summary>Writes or replaces a text file, creating parent folders. Used only for a user-chosen export path.</summary>
    void WriteText(string destinationPath, string content);

    bool FileExists(string path);
}

/// <summary>Privacy notice, storage report, index export, and the guarded delete-index action.</summary>
public interface IMaintenanceService
{
    Task<MaintenanceReport> GetReportAsync(CancellationToken cancellationToken);

    Task<IndexExportResult> ExportIndexAsync(IndexExportRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the application's index. Requires <paramref name="confirmed"/>; refuses while work is queued or
    /// leased; never deletes source media, quarantined copies, or the scan-root settings.
    /// </summary>
    Task<IndexDeletionResult> DeleteIndexAsync(bool confirmed, CancellationToken cancellationToken);
}
