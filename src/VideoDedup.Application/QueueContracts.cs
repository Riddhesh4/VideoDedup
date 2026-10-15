using System.Text.Json;

namespace VideoDedup.Application;

/// <summary>Durable work item kinds processed through the SQLite queue.</summary>
public enum JobType
{
    Discover,
    Probe,
    Hash,
    VisualFingerprint,
    AudioFingerprint,
    Match,
    Maintenance,
}

/// <summary>Lifecycle state of a queued job. Terminal states are <see cref="Completed"/> and <see cref="DeadLetter"/>.</summary>
public enum JobState
{
    Queued,
    Leased,
    Completed,
    DeadLetter,
}

/// <summary>Detached snapshot of a durable job. Claim/complete/fail transitions are atomic; no database transaction is held while a worker processes a returned job.</summary>
public sealed record QueueJob(
    Guid Id,
    Guid FileId,
    JobType JobType,
    JobState State,
    int Priority,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset CreatedUtc,
    DateTimeOffset NextVisibleUtc,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresUtc,
    string PayloadJson,
    string? DriveId,
    string? LastError);

/// <summary>Item for atomic batch enqueue.</summary>
public sealed record QueueEnqueueItem(
    Guid FileId,
    JobType JobType,
    string PayloadJson,
    EnqueueOptions? Options = null);

/// <summary>Options applied at enqueue time.</summary>
public sealed record EnqueueOptions(
    int Priority = 0,
    int MaxAttempts = 5,
    string? DriveId = null)
{
    public void Validate()
    {
        if (MaxAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "Max attempts must be between 1 and 100.");
        if (DriveId is not null)
        {
            var trimmed = DriveId.Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException("Drive ID must not be blank.", nameof(DriveId));
            if (trimmed.Any(char.IsControl))
                throw new ArgumentException("Drive ID cannot contain control characters.", nameof(DriveId));
        }
    }

    public string? NormalizedDriveId => string.IsNullOrWhiteSpace(DriveId) ? null : DriveId.Trim();
}

/// <summary>Worker claim request. Concurrency caps are evaluated against currently leased jobs.</summary>
public sealed record QueueClaimRequest(
    string WorkerId,
    TimeSpan LeaseDuration,
    int MaxJobs = 1,
    IReadOnlyList<JobType>? AllowedJobTypes = null,
    IReadOnlyList<string>? DriveScope = null,
    int? MaxPerDrive = null,
    int? MaxGlobal = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkerId))
            throw new ArgumentException("Worker ID must not be blank.", nameof(WorkerId));
        if (LeaseDuration <= TimeSpan.Zero || LeaseDuration > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), "Lease duration must be positive and at most 24 hours.");
        if (MaxJobs is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaxJobs), "Max jobs must be between 1 and 100.");
        if (MaxPerDrive is < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxPerDrive), "Per-drive limit must be positive.");
        if (MaxGlobal is < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxGlobal), "Global limit must be positive.");
    }
}

/// <summary>Queue depth snapshot.</summary>
public sealed record QueueStats(
    int Queued,
    int Leased,
    int Completed,
    int DeadLetter);

/// <summary>Injectable clock so retry/lease timing is deterministic in tests.</summary>
public interface IQueueClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock.</summary>
public sealed class SystemQueueClock : IQueueClock
{
    public static readonly SystemQueueClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Versioned payload validation. Payloads are never trusted; they are validated on enqueue and revalidated on every claim.</summary>
public static class QueuePayloadValidator
{
    public const int MaxPayloadChars = 64 * 1024;

    public const int CurrentVersion = 1;

    /// <returns>The payload schema version.</returns>
    public static int Validate(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("Job payload must not be blank.", nameof(payloadJson));
        if (payloadJson.Length > MaxPayloadChars)
            throw new ArgumentException($"Job payload exceeds the {MaxPayloadChars}-character limit.", nameof(payloadJson));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Job payload is not valid JSON.", nameof(payloadJson), exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Job payload must be a JSON object.", nameof(payloadJson));
            if (!document.RootElement.TryGetProperty("version", out var versionProperty) ||
                versionProperty.ValueKind != JsonValueKind.Number ||
                !versionProperty.TryGetInt32(out var version))
                throw new ArgumentException("Job payload must carry an integer \"version\" property.", nameof(payloadJson));
            if (version is < 1 or > CurrentVersion)
                throw new ArgumentException($"Job payload version {version} is not supported.", nameof(payloadJson));
            return version;
        }
    }
}

/// <summary>Bounded exponential backoff with deterministic per-job jitter, so retry timing is repeatable without randomness.</summary>
public static class QueueRetryPolicy
{
    public static TimeSpan ComputeDelay(Guid jobId, int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double jitterFactor = 0.2)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must be positive.");
        if (baseDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay must be positive.");
        if (maxDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be positive.");
        if (jitterFactor is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(jitterFactor), "Jitter factor must be between 0 and 1.");

        var exponentialTicks = (double)baseDelay.Ticks * Math.Pow(2, attempt - 1);
        var cappedTicks = Math.Min(exponentialTicks, (double)maxDelay.Ticks);

        // Deterministic FNV-1a jitter in [0, 1) derived from (jobId, attempt).
        var hash = 1469598103934665603ul;
        foreach (var value in jobId.ToByteArray())
        {
            hash ^= value;
            hash *= 1099511628211ul;
        }

        foreach (var value in BitConverter.GetBytes(attempt))
        {
            hash ^= value;
            hash *= 1099511628211ul;
        }

        var fraction = (hash >> 11) / (double)(1ul << 53);
        var factor = 1.0 + ((fraction * 2.0) - 1.0) * jitterFactor;
        var delayedTicks = Math.Clamp(cappedTicks * factor, 1.0, (double)maxDelay.Ticks);
        return TimeSpan.FromTicks((long)delayedTicks);
    }
}

/// <summary>Durable job queue port. All transitions are atomic and cancellation-safe; implementations must never hold a database transaction while a worker processes a job.</summary>
public interface IJobQueue
{
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Enqueues a job; returns the existing active job when one already exists for (FileId, JobType).</summary>
    Task<QueueJob> EnqueueAsync(Guid fileId, JobType jobType, string payloadJson, EnqueueOptions? options, CancellationToken cancellationToken);

    /// <summary>Enqueues a batch atomically: any invalid item aborts the whole batch with no rows persisted.</summary>
    Task<IReadOnlyList<QueueJob>> EnqueueBatchAsync(IReadOnlyList<QueueEnqueueItem> items, CancellationToken cancellationToken);

    /// <summary>Atomically recovers expired leases and leases up to <see cref="QueueClaimRequest.MaxJobs"/> visible jobs.</summary>
    Task<IReadOnlyList<QueueJob>> ClaimAsync(QueueClaimRequest request, CancellationToken cancellationToken);

    /// <summary>Marks a leased job complete. The lease owner must match.</summary>
    Task CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken);

    /// <summary>Records a transient failure: requeues with backoff, or moves to <see cref="JobState.DeadLetter"/> once attempts are exhausted. The lease owner must match.</summary>
    Task FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken);

    Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken);
}
