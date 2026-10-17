using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using VideoDedup.Application;

namespace VideoDedup.Desktop.ViewModels;

/// <summary>One row of the storage table: what an area holds and what deleting the index would do to it.</summary>
public sealed record StorageLine(string Area, string Size, string Detail);

/// <summary>
/// Privacy and data panel: what the application stores locally, exporting the index with or without file names, and
/// the confirmation-gated delete-index action. Permanent deletion of media is not expressible from here.
/// </summary>
public sealed class MaintenanceViewModel : ObservableObject
{
    public const string NoticeText =
        "Everything stays on this PC. File names, paths, fingerprints and match results are stored only in your own "
        + "Windows profile folder; nothing is uploaded, no account is needed, and no source file is ever deleted. "
        + "Deleting the index removes the database, discovery snapshots and logs. Quarantined copies and scan-root "
        + "settings are kept.";

    public const string DeleteConfirmation =
        "This deletes VideoDedup's index: the processing database, discovery snapshots and logs. Your video files are "
        + "never deleted, and quarantined copies stay where they are. Continue?";

    private readonly IMaintenanceService _maintenance;
    private readonly IConfirmationPrompt _confirmation;
    private string _exportPath;
    private bool _includePaths = true;
    private string _statusMessage = "Reading the local data summary...";
    private string? _errorMessage;
    private string _dataRootText = string.Empty;
    private string _retentionText = string.Empty;
    private bool _busy;

    public MaintenanceViewModel(IMaintenanceService maintenance, IConfirmationPrompt confirmation)
    {
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _exportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "videodedup-index.json");
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None), onError: Report);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !_busy, Report);
        DeleteIndexCommand = new AsyncRelayCommand(DeleteIndexAsync, () => !_busy, Report);
    }

    public ObservableCollection<StorageLine> Areas { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public AsyncRelayCommand DeleteIndexCommand { get; }

    public string ExportPath
    {
        get => _exportPath;
        set => SetProperty(ref _exportPath, value);
    }

    public bool IncludePaths
    {
        get => _includePaths;
        set => SetProperty(ref _includePaths, value);
    }

    public string DataRootText
    {
        get => _dataRootText;
        private set => SetProperty(ref _dataRootText, value);
    }

    public string RetentionText
    {
        get => _retentionText;
        private set => SetProperty(ref _retentionText, value);
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

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                ExportCommand.RaiseCanExecuteChanged();
                DeleteIndexCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        var report = await FillAreasAsync(cancellationToken).ConfigureAwait(true);
        StatusMessage = report.ReclaimableFileCount == 0
            ? $"Storing {FormatSize(report.TotalBytes)}; there is nothing to delete yet."
            : $"Storing {FormatSize(report.TotalBytes)}; deleting the index would free {FormatSize(report.ReclaimableBytes)} in {report.ReclaimableFileCount} file(s).";
    }

    public async Task ExportAsync()
    {
        var destination = ExportPath?.Trim() ?? string.Empty;
        if (destination.Length == 0)
        {
            StatusMessage = "Enter an export file path first.";
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _maintenance.ExportIndexAsync(new IndexExportRequest(destination, IncludePaths), CancellationToken.None))
                .ConfigureAwait(true);
            StatusMessage = $"Exported {result.FileCount} file record(s), {result.RootCount} scan root(s) and {result.MatchCount} match(es) to {result.DestinationPath} ({FormatSize(result.SizeBytes)})."
                + (result.IncludedPaths ? " File names and paths are included." : " File names and paths were left out.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteIndexAsync()
    {
        var confirmed = await _confirmation.ConfirmAsync("Delete index data", DeleteConfirmation, CancellationToken.None).ConfigureAwait(true);
        if (!confirmed)
        {
            StatusMessage = "Index deletion canceled; nothing was removed.";
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _maintenance.DeleteIndexAsync(true, CancellationToken.None)).ConfigureAwait(true);
            // Refresh the table first: the message about this action must survive the routine storage summary.
            await FillAreasAsync(CancellationToken.None).ConfigureAwait(true);
            StatusMessage = result.Failures.Count == 0
                ? $"Deleted {result.Deleted.Count} file(s) that the application can rebuild; kept {result.Retained.Count} entry(ies) (quarantined copies and settings)."
                : $"Deleted {result.Deleted.Count} file(s) and kept {result.Retained.Count} entry(ies), but {result.Failures.Count} file(s) could not be removed.";
            if (result.Failures.Count > 0)
            {
                ErrorMessage = string.Join("; ", result.Failures);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Surfaces a failure raised outside a command, such as the startup refresh.</summary>
    public void ReportExternalFailure(Exception exception) => Report(exception);

    /// <summary>Refreshes the storage table and the retention notes without touching the status line.</summary>
    private async Task<MaintenanceReport> FillAreasAsync(CancellationToken cancellationToken)
    {
        var report = await Task.Run(() => _maintenance.GetReportAsync(cancellationToken), cancellationToken).ConfigureAwait(true);
        Areas.Clear();
        foreach (var area in report.Areas)
        {
            var detail = area.ReclaimableBytes > 0
                ? $"deleting the index removes {FormatSize(area.ReclaimableBytes)}"
                : "kept when the index is deleted";
            Areas.Add(new StorageLine(Describe(area.Area), FormatSize(area.SizeBytes), $"{area.FileCount} file(s); {detail}"));
        }

        DataRootText = $"Application data folder: {report.DataRoot}";
        RetentionText = report.Notes.Count == 0 ? string.Empty : string.Join(" ", report.Notes);
        return report;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    private static string Describe(AppDataArea area) => area switch
    {
        AppDataArea.IndexDatabase => "Processing index (database)",
        AppDataArea.DiscoverySnapshots => "Discovery snapshots",
        AppDataArea.Configuration => "Settings and scan roots",
        AppDataArea.ProcessingLogs => "Processing logs",
        AppDataArea.QuarantineStore => "Quarantined files",
        _ => "Unrecognized files",
    };

    private void Report(Exception exception) => ErrorMessage = exception is OperationCanceledException
        ? "The operation was canceled."
        : $"The action failed ({exception.GetType().Name}).";
}
