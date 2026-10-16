namespace VideoDedup.Application;

/// <summary>Observable state of one hosted processing worker.</summary>
public enum WorkerRunState
{
    Idle,
    Running,
    Paused,
    Stopped,
}

/// <summary>Per-worker status shown in the desktop dashboard. Counts are per process lifetime.</summary>
public sealed record WorkerStatus(
    string Name,
    WorkerRunState State,
    int CompletedCount,
    int FailedCount,
    string? LastError,
    DateTimeOffset? LastActivityUtc);

/// <summary>
/// Pause/resume gate plus status source shared by the worker host, the desktop dashboard, and tests.
/// Pausing stops new claims; a job already leased finishes or is recovered by lease expiry, so pausing never
/// abandons half-processed work and never loses a completed result.
/// </summary>
public interface IWorkerControl
{
    bool IsPaused { get; }

    void Pause();

    void Resume();

    IReadOnlyList<WorkerStatus> Snapshot();

    void ReportRunning(string workerName);

    /// <summary>Records the end of an iteration; <paramref name="processed"/> is true when a job was handled.</summary>
    void ReportIdle(string workerName, bool processed);

    /// <summary>Records a recoverable iteration failure that the next iteration retries.</summary>
    void ReportFailure(string workerName, string error);

    void ReportStopped(string workerName);
}

/// <summary>Thread-safe in-memory implementation. Registered workers appear in snapshots before their first iteration.</summary>
public sealed class WorkerStatusRegistry : IWorkerControl
{
    private const int MaxErrorLength = 512;

    private readonly object _gate = new();
    private readonly Dictionary<string, MutableStatus> _workers = new(StringComparer.Ordinal);
    private readonly IQueueClock _clock;
    private bool _paused;

    public WorkerStatusRegistry(IEnumerable<string> workerNames, IQueueClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(workerNames);
        _clock = clock ?? SystemQueueClock.Instance;
        foreach (var name in workerNames)
        {
            var normalized = name?.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                throw new ArgumentException("Worker names must not be blank.", nameof(workerNames));
            }

            if (!_workers.TryAdd(normalized, new MutableStatus()))
            {
                throw new ArgumentException($"Worker '{normalized}' is registered twice.", nameof(workerNames));
            }
        }
    }

    public bool IsPaused
    {
        get { lock (_gate) { return _paused; } }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            foreach (var status in _workers.Values)
            {
                if (status.State is WorkerRunState.Idle or WorkerRunState.Running)
                {
                    status.State = WorkerRunState.Paused;
                }
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            foreach (var status in _workers.Values)
            {
                if (status.State == WorkerRunState.Paused)
                {
                    status.State = WorkerRunState.Idle;
                }
            }
        }
    }

    public void ReportRunning(string workerName)
    {
        lock (_gate)
        {
            var status = Require(workerName);
            status.State = _paused ? WorkerRunState.Paused : WorkerRunState.Running;
            status.LastActivityUtc = _clock.UtcNow;
        }
    }

    public void ReportIdle(string workerName, bool processed)
    {
        lock (_gate)
        {
            var status = Require(workerName);
            if (processed)
            {
                status.CompletedCount++;
            }

            status.LastError = null;
            status.State = _paused ? WorkerRunState.Paused : WorkerRunState.Idle;
            status.LastActivityUtc = _clock.UtcNow;
        }
    }

    public void ReportFailure(string workerName, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var normalized = error.Trim();
        if (normalized.Length > MaxErrorLength)
        {
            normalized = normalized[..MaxErrorLength];
        }

        lock (_gate)
        {
            var status = Require(workerName);
            status.FailedCount++;
            status.LastError = normalized;
            status.State = _paused ? WorkerRunState.Paused : WorkerRunState.Idle;
            status.LastActivityUtc = _clock.UtcNow;
        }
    }

    public void ReportStopped(string workerName)
    {
        lock (_gate)
        {
            var status = Require(workerName);
            status.State = WorkerRunState.Stopped;
            status.LastActivityUtc = _clock.UtcNow;
        }
    }

    public IReadOnlyList<WorkerStatus> Snapshot()
    {
        lock (_gate)
        {
            return _workers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new WorkerStatus(pair.Key, pair.Value.State, pair.Value.CompletedCount,
                    pair.Value.FailedCount, pair.Value.LastError, pair.Value.LastActivityUtc))
                .ToArray();
        }
    }

    private MutableStatus Require(string workerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);
        return _workers.TryGetValue(workerName, out var status)
            ? status
            : throw new ArgumentException($"Worker '{workerName}' is not registered.", nameof(workerName));
    }

    private sealed class MutableStatus
    {
        public WorkerRunState State { get; set; } = WorkerRunState.Idle;
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset? LastActivityUtc { get; set; }
    }
}
