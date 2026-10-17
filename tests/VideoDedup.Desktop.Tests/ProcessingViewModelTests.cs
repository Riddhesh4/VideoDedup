using VideoDedup.Application;
using VideoDedup.Desktop.ViewModels;

namespace VideoDedup.Desktop.Tests;

public sealed class ProcessingViewModelTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refreshing_shows_queue_depth_worker_states_and_recoverable_failures()
    {
        var dashboard = new FakeDashboard();
        dashboard.Rows.Add(new ProcessingRow("clip.mkv", "Complete", "abc", "h264", "1/1 usable", "Fingerprint ready"));
        var viewModel = new ProcessingViewModel(dashboard);

        await viewModel.RefreshAsync(Token);

        viewModel.Rows.Should().ContainSingle().Which.Path.Should().Be("clip.mkv");
        viewModel.Workers.Should().HaveCount(2);
        viewModel.Workers[1].LastError.Should().Contain("InvalidData");
        viewModel.StatusMessage.Should().Contain("Queued: 3");
        viewModel.StatusMessage.Should().Contain("Failed jobs: 2");
        viewModel.StatusMessage.Should().Contain("Retrying: Match");
    }

    [Fact]
    public async Task Refreshing_tracks_queued_work_for_the_review_empty_state()
    {
        var dashboard = new FakeDashboard { Stats = new QueueStats(2, 0, 0, 0) };
        var viewModel = new ProcessingViewModel(dashboard);

        await viewModel.RefreshAsync(Token);

        viewModel.HasPendingWork.Should().BeTrue();
        viewModel.IsBusy.Should().BeTrue();

        dashboard.Stats = new QueueStats(0, 0, 2, 0);
        await viewModel.RefreshAsync(Token);

        viewModel.HasPendingWork.Should().BeFalse();
        viewModel.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void Pausing_and_resuming_workers_toggles_command_availability()
    {
        var dashboard = new FakeDashboard();
        var viewModel = new ProcessingViewModel(dashboard);

        viewModel.PauseCommand.CanExecute(null).Should().BeTrue();
        viewModel.ResumeCommand.CanExecute(null).Should().BeFalse();

        viewModel.PauseCommand.Execute(null);

        dashboard.Pauses.Should().Be(1);
        viewModel.IsPaused.Should().BeTrue();
        viewModel.PauseCommand.CanExecute(null).Should().BeFalse();
        viewModel.ResumeCommand.CanExecute(null).Should().BeTrue();
        viewModel.StatusMessage.Should().Contain("paused");

        viewModel.ResumeCommand.Execute(null);

        dashboard.Resumes.Should().Be(1);
        viewModel.IsPaused.Should().BeFalse();
        viewModel.StatusMessage.Should().Contain("resumed");
    }

    [Fact]
    public async Task Scanning_requires_a_folder_and_never_calls_the_dashboard_without_one()
    {
        var dashboard = new FakeDashboard();
        var viewModel = new ProcessingViewModel(dashboard);

        await viewModel.ScanAsync();

        dashboard.Scans.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("Enter a folder path");

        viewModel.Folder = @"C:\Videos";
        await viewModel.ScanAsync();

        dashboard.Scans.Should().Equal(@"C:\Videos");
        viewModel.IsScanning.Should().BeFalse();
        viewModel.StatusMessage.Should().Contain("Scan finished");
    }

    [Fact]
    public async Task Cancelling_a_scan_keeps_queued_work_and_reports_the_cancellation()
    {
        var dashboard = new FakeDashboard();
        dashboard.ScanBehavior = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken);
        var viewModel = new ProcessingViewModel(dashboard)
        {
            Folder = @"C:\Videos",
        };

        var scan = viewModel.ScanAsync();
        viewModel.IsScanning.Should().BeTrue();
        viewModel.CancelScan();
        await scan;

        viewModel.StatusMessage.Should().Contain("canceled; queued work is retained");
        viewModel.IsScanning.Should().BeFalse();
    }

    [Fact]
    public async Task Dashboard_failures_surface_as_text_instead_of_crashing()
    {
        var viewModel = new ProcessingViewModel(new ThrowingDashboard());

        await viewModel.RefreshAsync(Token);

        viewModel.ErrorMessage.Should().Contain("InvalidOperationException", "only the exception type is shown so a message can never leak a local path");
        viewModel.StatusMessage.Should().Be("Ready.");
    }

    [Fact]
    public void Commands_are_initialized_and_scan_can_be_cancelled_only_while_scanning()
    {
        var viewModel = new ProcessingViewModel(new FakeDashboard());

        viewModel.ScanCommand.Should().NotBeNull();
        viewModel.RefreshCommand.Should().NotBeNull();
        viewModel.CancelCommand.CanExecute(null).Should().BeFalse();
    }

    private sealed class ThrowingDashboard : IProcessingDashboard
    {
        public Task ScanAsync(string folder, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ProcessingRow>> RefreshAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Refresh failed");

        public Task<QueueStats> StatsAsync(CancellationToken cancellationToken) => Task.FromResult(new QueueStats(0, 0, 0, 0));

        public IReadOnlyList<WorkerStatus> WorkerStates() => Array.Empty<WorkerStatus>();

        public void PauseWorkers()
        {
        }

        public void ResumeWorkers()
        {
        }
    }
}
