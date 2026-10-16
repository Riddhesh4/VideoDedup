using VideoDedup.Domain;

namespace VideoDedup.Application;

/// <summary>Durable lifecycle of one quarantined file.</summary>
public enum QuarantineState
{
    /// <summary>Intent recorded before the move, so an interruption is always explainable.</summary>
    Pending,
    Quarantined,
    Restored,
    Failed,
}

/// <summary>Typed storage failure so callers can distinguish a missing file from a denied or conflicting one.</summary>
public enum StorageFailure
{
    NotFound,
    AccessDenied,
    Conflict,
    Unsupported,
    Unknown,
}

public sealed class StorageException(StorageFailure failure, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    public StorageFailure Failure { get; } = failure;
}

/// <summary>Read-only result of looking at one path. Nothing here ever follows a link to another file.</summary>
public sealed record StorageInspection(bool Exists, bool IsSymbolicLink, bool IsDirectory, long SizeBytes, DateTimeOffset? ModifiedUtc);

/// <summary>
/// Safe filesystem operations for quarantine and undo. Implementations must never overwrite an existing file,
/// never follow reparse points, and must leave the source intact until a cross-volume copy is fully written.
/// </summary>
public interface IQuarantineStorage
{
    StorageInspection Inspect(string path);

    bool DirectoryExists(string path);

    bool IsWritableDirectory(string path);

    /// <summary>Volume root of a path, or null when it cannot be determined (used for same-volume decisions).</summary>
    string? GetVolumeRoot(string path);

    void EnsureDirectory(string path);

    /// <summary>Moves a file without overwriting anything; throws <see cref="StorageException"/> on failure.</summary>
    void Move(string source, string destination);
}

/// <summary>Durable quarantine audit log. A row is written before every move, so an interrupted move is recoverable.</summary>
public interface IQuarantineLog
{
    Task<QuarantineRecord> BeginAsync(Guid fileId, string sourcePath, string destinationPath, bool crossVolume, CancellationToken cancellationToken);

    Task MarkQuarantinedAsync(Guid recordId, CancellationToken cancellationToken);

    Task MarkRestoredAsync(Guid recordId, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid recordId, string reason, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantineRecord>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>One durable quarantine entry with its undo information.</summary>
public sealed record QuarantineRecord
{
    private QuarantineRecord(Guid recordId, Guid fileId, string sourcePath, string destinationPath,
        QuarantineState state, bool crossVolume, DateTimeOffset createdUtc, DateTimeOffset? completedUtc, string? failureReason)
    {
        RecordId = recordId;
        FileId = fileId;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        State = state;
        CrossVolume = crossVolume;
        CreatedUtc = createdUtc;
        CompletedUtc = completedUtc;
        FailureReason = failureReason;
    }

    public Guid RecordId { get; }
    public Guid FileId { get; }
    public string SourcePath { get; }
    public string DestinationPath { get; }
    public QuarantineState State { get; }
    public bool CrossVolume { get; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset? CompletedUtc { get; }
    public string? FailureReason { get; }

    public static QuarantineRecord Create(Guid recordId, Guid fileId, string sourcePath, string destinationPath,
        QuarantineState state, bool crossVolume, DateTimeOffset createdUtc, DateTimeOffset? completedUtc = null,
        string? failureReason = null)
    {
        if (recordId == Guid.Empty || fileId == Guid.Empty)
        {
            throw new ArgumentException("A quarantine record needs non-empty record and file IDs.");
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ValidatePath(sourcePath, nameof(sourcePath));
        ValidatePath(destinationPath, nameof(destinationPath));
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A quarantine destination must differ from the source.", nameof(destinationPath));
        }

        var reason = failureReason?.Trim();
        if (reason is not null && reason.Length > 400)
        {
            reason = reason[..400];
        }

        return new QuarantineRecord(recordId, fileId, sourcePath, destinationPath, state, crossVolume,
            createdUtc.ToUniversalTime(), completedUtc?.ToUniversalTime(), string.IsNullOrEmpty(reason) ? null : reason);
    }

    /// <summary>Transition copy used by the log implementation and its tests.</summary>
    public QuarantineRecord WithState(QuarantineState state, DateTimeOffset completedUtc, string? failureReason = null) =>
        Create(RecordId, FileId, SourcePath, DestinationPath, state, CrossVolume, CreatedUtc, completedUtc, failureReason);

    private static void ValidatePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Quarantine paths must be absolute.", parameterName);
        }

        if (path.Any(char.IsControl))
        {
            throw new ArgumentException("Quarantine paths cannot contain control characters.", parameterName);
        }
    }
}

/// <summary>One planned quarantine action: allowed, or blocked with the reason the user must be told about.</summary>
public sealed record QuarantinePlanItem(
    Guid FileId,
    string SourcePath,
    string DisplayName,
    bool IsAllowed,
    QuarantineBlockReason? BlockedBy,
    IReadOnlyList<string> Reasons,
    string? DestinationPath,
    bool CrossVolume);

/// <summary>Complete plan shown to the user before anything moves. Blocked items are always reported, never silently skipped.</summary>
public sealed record QuarantinePlan(IReadOnlyList<QuarantinePlanItem> Items)
{
    public IReadOnlyList<QuarantinePlanItem> AllowedItems => Items.Where(item => item.IsAllowed).ToArray();

    public IReadOnlyList<QuarantinePlanItem> BlockedItems => Items.Where(item => !item.IsAllowed).ToArray();

    /// <summary>True when the batch must be confirmed explicitly before any file moves.</summary>
    public bool RequiresConfirmation => Items.Any(item => item.IsAllowed);

    public bool SpansVolumes => AllowedItems.Any(item => item.CrossVolume);
}

/// <summary>Result of one attempted quarantine action.</summary>
public sealed record QuarantineOutcome(Guid FileId, bool Succeeded, string? Failure, Guid? RecordId, bool CrossVolume)
{
    public static QuarantineOutcome Moved(Guid fileId, Guid recordId, bool crossVolume) => new(fileId, true, null, recordId, crossVolume);

    public static QuarantineOutcome Blocked(Guid fileId, string failure) => new(fileId, false, failure, null, false);
}

/// <summary>
/// Batch result. <see cref="ConfirmationRequired"/> means nothing was moved because the caller did not confirm.
/// </summary>
public sealed record QuarantineBatchResult(bool ConfirmationRequired, IReadOnlyList<QuarantineOutcome> Outcomes)
{
    public IReadOnlyList<QuarantineOutcome> Moved => Outcomes.Where(outcome => outcome.Succeeded).ToArray();

    public IReadOnlyList<QuarantineOutcome> Failures => Outcomes.Where(outcome => !outcome.Succeeded).ToArray();
}

/// <summary>Outcome of resolving quarantine rows that were interrupted by a crash or power loss.</summary>
public sealed record QuarantineRecoveryReport(IReadOnlyList<QuarantineRecord> Resolved, IReadOnlyList<QuarantineRecord> NeedsAttention);

/// <summary>Everything the user can do to a review selection: plan, execute with confirmation, undo, and recover.</summary>
public interface IQuarantineService
{
    Task<QuarantinePlan> PlanAsync(IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken);

    Task<QuarantineBatchResult> ExecuteAsync(QuarantinePlan plan, bool confirmed, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantineRecord>> GetHistoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantineOutcome>> UndoAsync(IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken);

    Task<QuarantineRecoveryReport> RecoverInterruptedAsync(CancellationToken cancellationToken);
}
