using VideoDedup.Application;

namespace VideoDedup.Application.Tests;

public sealed class DisplayTextTests
{
    [Fact]
    public void Control_characters_and_direction_overrides_are_removed_from_displayed_names()
    {
        var hostile = "e\u0007vil\u001b[31m\u202Egnp.exe \uFEFF";

        var sanitized = DisplayText.Sanitize(hostile);

        sanitized.Should().Be("evil[31mgnp.exe");
        sanitized.Should().NotContain("\u001b");
        sanitized.Should().NotContain("\u202E");
    }

    [Fact]
    public void Whitespace_is_collapsed_and_trimmed()
    {
        DisplayText.Sanitize("  a \t b \n c  ").Should().Be("a b c");
        DisplayText.Sanitize("   ").Should().BeEmpty();
        DisplayText.Sanitize(null).Should().BeEmpty();
    }

    [Fact]
    public void Emoji_and_astral_characters_survive_sanitization()
    {
        DisplayText.Sanitize("clip \U0001F3AC \u2603.mkv").Should().Be("clip \U0001F3AC \u2603.mkv");
    }

    [Fact]
    public void Long_values_are_truncated_with_an_explicit_marker()
    {
        var sanitized = DisplayText.Sanitize(new string('x', 400), 32);

        sanitized.Should().HaveLength(35);
        sanitized.Should().EndWith("...");
        FluentActions.Invoking(() => DisplayText.Sanitize("value", 2)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void File_names_are_extracted_from_windows_and_posix_paths()
    {
        DisplayText.FileName(@"C:\Videos\Family\holiday\clip.mkv").Should().Be("clip.mkv");
        DisplayText.FileName("videos/clip.mkv").Should().Be("clip.mkv");
        DisplayText.FileName(@"C:\folder\").Should().BeEmpty();
    }
}

public sealed class IndexedPathTests
{
    [Fact]
    public void Safe_relative_paths_join_under_their_root()
    {
        IndexedPath.TryCombine(@"C:\Videos", @"Family\holiday\clip.mkv", out var full).Should().BeTrue();
        full.Should().Be(@"C:\Videos\Family\holiday\clip.mkv");
        IndexedPath.TryCombine(@"C:\Videos", "clip \u2603.mkv", out var unicode).Should().BeTrue();
        unicode.Should().Contain("\u2603");
    }

    [Theory]
    [InlineData(@"..\escape.mkv")]
    [InlineData("../../escape.mkv")]
    [InlineData(@"C:\Windows\system32\evil.mkv")]
    [InlineData("/etc/passwd")]
    [InlineData("folder//clip.mkv")]
    [InlineData("folder/./clip.mkv")]
    [InlineData("")]
    [InlineData("bad\u0001name.mkv")]
    public void Traversal_rooted_and_malformed_paths_are_refused(string relative)
    {
        IndexedPath.TryCombine(@"C:\Videos", relative, out var full).Should().BeFalse();
        full.Should().BeEmpty();
    }

    [Fact]
    public void Rooted_roots_and_blank_roots_are_refused()
    {
        IndexedPath.TryCombine(null, "clip.mkv", out _).Should().BeFalse();
        IndexedPath.TryCombine("  ", "clip.mkv", out _).Should().BeFalse();
        IndexedPath.TryCombine(@"C:\Videos", null, out _).Should().BeFalse();
    }
}

public sealed class WorkerControlTests
{
    [Fact]
    public void Workers_appear_in_snapshot_order_with_counts_and_last_error()
    {
        var registry = new WorkerStatusRegistry(["Hash", "Probe"]);

        registry.Snapshot().Select(status => status.Name).Should().Equal("Hash", "Probe");
        registry.ReportRunning("Probe");
        registry.ReportIdle("Probe", processed: true);
        registry.ReportFailure("Hash", "MediaProcessingException: InvalidData");

        var probe = registry.Snapshot().Single(status => status.Name == "Probe");
        probe.State.Should().Be(WorkerRunState.Idle);
        probe.CompletedCount.Should().Be(1);
        probe.LastActivityUtc.Should().NotBeNull();
        var hash = registry.Snapshot().Single(status => status.Name == "Hash");
        hash.FailedCount.Should().Be(1);
        hash.CompletedCount.Should().Be(0);
        hash.LastError.Should().Be("MediaProcessingException: InvalidData");
    }

    [Fact]
    public void Pausing_marks_idle_and_running_workers_paused_and_resuming_clears_it()
    {
        var registry = new WorkerStatusRegistry(["Probe"]);
        registry.ReportRunning("Probe");

        registry.Pause();

        registry.IsPaused.Should().BeTrue();
        registry.Snapshot().Single().State.Should().Be(WorkerRunState.Paused);
        registry.ReportIdle("Probe", processed: false);
        registry.Snapshot().Single().State.Should().Be(WorkerRunState.Paused, "a completing iteration cannot un-pause the worker");

        registry.Resume();

        registry.IsPaused.Should().BeFalse();
        registry.Snapshot().Single().State.Should().Be(WorkerRunState.Idle);
    }

    [Fact]
    public void Stopping_marks_a_worker_stopped_and_a_success_clears_the_last_error()
    {
        var registry = new WorkerStatusRegistry(["Probe"]);
        registry.ReportFailure("Probe", "boom");
        registry.ReportIdle("Probe", processed: true);

        registry.Snapshot().Single().LastError.Should().BeNull();
        registry.ReportStopped("Probe");
        registry.Snapshot().Single().State.Should().Be(WorkerRunState.Stopped);
    }

    [Fact]
    public void Reporting_errors_are_truncated_and_unregistered_workers_are_rejected()
    {
        var registry = new WorkerStatusRegistry(["Probe"]);

        registry.ReportFailure("Probe", new string('e', 900));
        registry.Snapshot().Single().LastError.Should().HaveLength(512);

        FluentActions.Invoking(() => registry.ReportIdle("Missing", processed: true)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new WorkerStatusRegistry(["Probe", "Probe"])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new WorkerStatusRegistry([" "])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_registry_reports_its_own_clock_so_pause_state_is_testable()
    {
        var registry = new WorkerStatusRegistry(["Probe"], new SystemQueueClock());
        registry.ReportRunning("Probe");

        registry.Snapshot().Single().LastActivityUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }
}
