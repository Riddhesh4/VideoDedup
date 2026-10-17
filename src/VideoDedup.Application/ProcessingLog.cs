namespace VideoDedup.Application;

/// <summary>Outcome of one worker iteration. There is deliberately no "unknown": every iteration is explained.</summary>
public enum ProcessingOutcome
{
    Processed,
    Idle,
    Failed,
}

/// <summary>
/// One durable log line. It carries counts, durations, and a failure *kind*; it can never carry a path, a file
/// name, or a free-form message, because those are the user's private data.
/// </summary>
public sealed record ProcessingLogEvent(
    DateTimeOffset TimestampUtc,
    string Worker,
    ProcessingOutcome Outcome,
    long ElapsedMilliseconds,
    string? FailureKind)
{
    public const int MaxWorkerLength = 64;

    public static ProcessingLogEvent Create(string worker, ProcessingOutcome outcome, TimeSpan elapsed,
        string? failureKind = null, IQueueClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Elapsed time must be between zero and 24 hours.");
        }

        var name = PrivacyText.Sanitize(worker);
        if (name.Length == 0)
        {
            throw new ArgumentException("A worker name must contain visible characters.", nameof(worker));
        }

        if (name.Length > MaxWorkerLength)
        {
            name = name[..MaxWorkerLength];
        }

        return new ProcessingLogEvent((clock ?? SystemQueueClock.Instance).UtcNow, name, outcome,
            (long)elapsed.TotalMilliseconds, PrivacyText.RedactPathsOptional(failureKind));
    }
}

/// <summary>
/// Sink for privacy-safe processing events. Implementations must never throw into a worker loop: losing a log line
/// is acceptable, stopping the scan is not.
/// </summary>
public interface IProcessingLog
{
    void Write(ProcessingLogEvent entry);
}

/// <summary>Log sink for tests and for callers that have no durable log.</summary>
public sealed class NullProcessingLog : IProcessingLog
{
    public static readonly NullProcessingLog Instance = new();

    private NullProcessingLog()
    {
    }

    public void Write(ProcessingLogEvent entry)
    {
    }
}

/// <summary>Per-worker totals for the dashboard.</summary>
public sealed record WorkerThroughput(string Worker, int Processed, int Failures);

/// <summary>Bounded, privacy-safe view of what the workers have done.</summary>
public sealed record ProcessingMetricsSnapshot(
    long Iterations,
    long JobsProcessed,
    long Failures,
    double JobsPerMinute,
    TimeSpan Window,
    int QueueDepth,
    IReadOnlyList<WorkerThroughput> Workers);

/// <summary>
/// Accumulates processing events in memory for the dashboard and the maintenance report. Throughput is a sliding
/// average over a bounded window, so it reacts to a stalled drive without keeping unbounded history.
/// </summary>
public sealed class ProcessingMetrics : IProcessingLog
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private const int MaxSamples = 8192;

    private readonly object _gate = new();
    private readonly IQueueClock _clock;
    private readonly TimeSpan _window;
    private readonly IProcessingLog? _sink;
    private readonly Dictionary<string, MutableThroughput> _workers = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _processed = new();
    private long _iterations;
    private long _jobsProcessed;
    private long _failures;

    public ProcessingMetrics(IQueueClock? clock = null, TimeSpan? window = null, IProcessingLog? sink = null)
    {
        _clock = clock ?? SystemQueueClock.Instance;
        _window = window ?? DefaultWindow;
        _sink = sink;
        if (_window <= TimeSpan.Zero || _window > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The metrics window must be between zero and one hour.");
        }
    }

    public void Write(ProcessingLogEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // The durable sink and the counters are both diagnostics: neither may stop a worker.
        try
        {
            _sink?.Write(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        lock (_gate)
        {
            _iterations++;
            if (!_workers.TryGetValue(entry.Worker, out var worker))
            {
                _workers[entry.Worker] = worker = new MutableThroughput();
            }

            if (entry.Outcome == ProcessingOutcome.Failed)
            {
                _failures++;
                worker.Failures++;
            }
            else if (entry.Outcome == ProcessingOutcome.Processed)
            {
                _jobsProcessed++;
                worker.Processed++;
                _processed.Enqueue(entry.TimestampUtc);
                while (_processed.Count > MaxSamples || (_processed.Count > 0 && entry.TimestampUtc - _processed.Peek() > _window))
                {
                    _processed.Dequeue();
                }
            }
        }
    }

    /// <summary><paramref name="queueDepth"/> is supplied by the caller because the queue, not the log, owns it.</summary>
    public ProcessingMetricsSnapshot Snapshot(int queueDepth = 0)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            while (_processed.Count > 0 && now - _processed.Peek() > _window)
            {
                _processed.Dequeue();
            }

            var workers = _workers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new WorkerThroughput(pair.Key, pair.Value.Processed, pair.Value.Failures))
                .ToArray();
            return new ProcessingMetricsSnapshot(_iterations, _jobsProcessed, _failures,
                Math.Round(_processed.Count / _window.TotalMinutes, 2), _window, Math.Max(queueDepth, 0), workers);
        }
    }

    private sealed class MutableThroughput
    {
        public int Processed { get; set; }

        public int Failures { get; set; }
    }
}
