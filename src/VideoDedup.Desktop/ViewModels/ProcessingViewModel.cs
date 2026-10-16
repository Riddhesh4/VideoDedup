using System.Collections.ObjectModel;
using VideoDedup.Application;

namespace VideoDedup.Desktop.ViewModels;

/// <summary>
/// Processing dashboard: indexed rows, queue depth, per-worker state, and pause/resume/cancel. Read-only with
/// respect to user files; scanning only enqueues work.
/// </summary>
public sealed class ProcessingViewModel : ObservableObject
{
    private readonly IProcessingDashboard _dashboard;
    private CancellationTokenSource _scan = new();
    private string _folder = string.Empty;
    private string _statusMessage = "Ready.";
    private string? _errorMessage;
    private bool _scanning;
    private bool _paused;

    public ProcessingViewModel(IProcessingDashboard dashboard)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !_scanning, Report);
        CancelCommand = new RelayCommand(CancelScan, () => _scanning);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None), onError: Report);
        PauseCommand = new RelayCommand(PauseWorkers, () => !_paused);
        ResumeCommand = new RelayCommand(ResumeWorkers, () => _paused);
    }

    public ObservableCollection<ProcessingRow> Rows { get; } = new();

    public ObservableCollection<WorkerStatus> Workers { get; } = new();

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public string Folder
    {
        get => _folder;
        set => SetProperty(ref _folder, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsScanning
    {
        get => _scanning;
        private set
        {
            if (SetProperty(ref _scanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPaused
    {
        get => _paused;
        private set
        {
            if (SetProperty(ref _paused, value))
            {
                PauseCommand.RaiseCanExecuteChanged();
                ResumeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Cancels the running scan; queued work is retained and resumes when a worker claims it.</summary>
    public void CancelScan() => _scan.Cancel();

    public async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(Folder))
        {
            StatusMessage = "Enter a folder path first.";
            return;
        }

        ErrorMessage = null;
        _scan.Dispose();
        _scan = new CancellationTokenSource();
        IsScanning = true;
        string? message = null;
        try
        {
            var folder = Folder;
            await Task.Run(() => _dashboard.ScanAsync(folder, _scan.Token), _scan.Token).ConfigureAwait(true);
            message = "Scan finished; queued work is processed by the workers below.";
        }
        catch (OperationCanceledException)
        {
            message = "Scan canceled; queued work is retained and resumes when a worker claims it.";
        }
        catch (Exception exception)
        {
            Report(exception);
        }
        finally
        {
            IsScanning = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            // The action message wins over the periodic queue summary, so the user sees what just happened.
            if (message is not null)
            {
                StatusMessage = message;
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _dashboard.RefreshAsync(cancellationToken).ConfigureAwait(true);
            var stats = await _dashboard.StatsAsync(cancellationToken).ConfigureAwait(true);
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            Workers.Clear();
            foreach (var worker in _dashboard.WorkerStates())
            {
                Workers.Add(worker);
            }

            var failures = Workers.Where(worker => worker.FailedCount > 0).Select(worker => worker.Name).ToArray();
            StatusMessage = $"Queued: {stats.Queued}  Processing: {stats.Leased}  Completed: {stats.Completed}  Failed jobs: {stats.DeadLetter}" +
                (Workers.Count == 0 ? string.Empty : $"  |  Workers: {Workers.Count}") +
                (failures.Length == 0 ? string.Empty : $"  |  Retrying: {string.Join(", ", failures)}");
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    public void PauseWorkers()
    {
        _dashboard.PauseWorkers();
        IsPaused = true;
        StatusMessage = "Workers paused. Queued work is retained and resumes when you continue.";
    }

    public void ResumeWorkers()
    {
        _dashboard.ResumeWorkers();
        IsPaused = false;
        StatusMessage = "Workers resumed.";
    }

    private void Report(Exception exception) =>
        ErrorMessage = exception is OperationCanceledException
            ? "The operation was canceled."
            : $"The action failed ({exception.GetType().Name}).";
}
