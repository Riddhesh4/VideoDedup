using VideoDedup.Application;

namespace VideoDedup.Application.Tests;

public sealed class ProcessingMetricsTests
{
    private sealed class TestClock : IQueueClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void A_log_event_never_carries_a_path_and_bounds_its_fields()
    {
        var entry = ProcessingLogEvent.Create("Probe", ProcessingOutcome.Failed, TimeSpan.FromMilliseconds(1200),
            @"MediaProcessingException: Missing C:\Users\bob\private\x.mkv");

        entry.Worker.Should().Be("Probe");
        entry.Outcome.Should().Be(ProcessingOutcome.Failed);
        entry.ElapsedMilliseconds.Should().Be(1200);
        entry.FailureKind.Should().Contain("MediaProcessingException");
        entry.FailureKind.Should().NotContain("bob");
        entry.FailureKind!.Length.Should().BeLessThanOrEqualTo(PrivacyText.MaxFailureLength);
        entry.TimestampUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void A_log_event_rejects_nonsense_instead_of_writing_it()
    {
        Assert.Throws<ArgumentException>(() =>
            ProcessingLogEvent.Create("  ", ProcessingOutcome.Idle, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProcessingLogEvent.Create("Probe", ProcessingOutcome.Idle, TimeSpan.FromDays(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProcessingLogEvent.Create("Probe", (ProcessingOutcome)42, TimeSpan.Zero));
    }

    [Fact]
    public void Metrics_count_iterations_jobs_failures_and_per_worker_totals()
    {
        var clock = new TestClock();
        var metrics = new ProcessingMetrics(clock);
        metrics.Write(ProcessingLogEvent.Create("Probe", ProcessingOutcome.Processed, TimeSpan.FromMilliseconds(50), clock: clock));
        metrics.Write(ProcessingLogEvent.Create("Hash", ProcessingOutcome.Processed, TimeSpan.FromMilliseconds(80), clock: clock));
        metrics.Write(ProcessingLogEvent.Create("Probe", ProcessingOutcome.Idle, TimeSpan.Zero, clock: clock));
        metrics.Write(ProcessingLogEvent.Create("Probe", ProcessingOutcome.Failed, TimeSpan.FromMilliseconds(10), "MediaProcessingException: Timeout", clock));

        var snapshot = metrics.Snapshot(queueDepth: 3);

        snapshot.Iterations.Should().Be(4);
        snapshot.JobsProcessed.Should().Be(2);
        snapshot.Failures.Should().Be(1);
        snapshot.QueueDepth.Should().Be(3);
        snapshot.Workers.Should().Equal(new WorkerThroughput("Hash", 1, 0), new WorkerThroughput("Probe", 1, 1));
    }

    [Fact]
    public void Throughput_is_a_sliding_window_so_a_stalled_drive_stops_being_counted()
    {
        var clock = new TestClock();
        var metrics = new ProcessingMetrics(clock, TimeSpan.FromMinutes(5));
        for (var index = 0; index < 10; index++)
        {
            metrics.Write(ProcessingLogEvent.Create("probe", ProcessingOutcome.Processed, TimeSpan.FromMilliseconds(10), clock: clock));
        }

        metrics.Snapshot().JobsPerMinute.Should().Be(2.0);

        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        metrics.Snapshot().JobsPerMinute.Should().Be(0);
        metrics.Snapshot().JobsProcessed.Should().Be(10, "lifetime counters are not reset by the window");
    }

    [Fact]
    public void Metrics_reject_an_unusable_window()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessingMetrics(window: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessingMetrics(window: TimeSpan.FromHours(2)));
    }
}
