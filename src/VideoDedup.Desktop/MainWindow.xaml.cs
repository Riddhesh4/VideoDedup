using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoDedup.Desktop.ViewModels;

namespace VideoDedup.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshing;
    private int _lastMatchCompleted = -1;

    public MainWindow(ProcessingViewModel processing, ReviewViewModel review, MaintenanceViewModel maintenance)
    {
        InitializeComponent();
        Processing = processing;
        Review = review;
        Maintenance = maintenance;
        DataContext = this;
        _timer.Tick += (_, _) => Tick();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            Processing.CancelScan();
        };
        _timer.Start();
    }

    /// <summary>Bound by XAML as <c>{Binding Processing.*}</c>.</summary>
    public ProcessingViewModel Processing { get; }

    /// <summary>Bound by XAML as <c>{Binding Review.*}</c>.</summary>
    public ReviewViewModel Review { get; }

    /// <summary>Bound by XAML as <c>{Binding Maintenance.*}</c>.</summary>
    public MaintenanceViewModel Maintenance { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Quarantine recovery runs once at startup so an interrupted move is resolved before review begins.
        Loaded -= OnLoaded;
        try
        {
            await Review.RecoverThenRefreshAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Review.ReportExternalFailure(exception);
        }

        await Processing.RefreshAsync(CancellationToken.None);
        Review.IsProcessing = Processing.IsBusy;
        _lastMatchCompleted = MatchCompletedCount();
        try
        {
            await Maintenance.RefreshAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Maintenance.ReportExternalFailure(exception);
        }
    }

    private async void Tick()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            await Processing.RefreshAsync(CancellationToken.None);
            Review.IsProcessing = Processing.IsBusy;
            var matchCompleted = MatchCompletedCount();
            if (matchCompleted != _lastMatchCompleted)
            {
                _lastMatchCompleted = matchCompleted;
                try
                {
                    await Review.RefreshAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    Review.ReportExternalFailure(exception);
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private int MatchCompletedCount() =>
        Processing.Workers.FirstOrDefault(worker => worker.Name == "Match")?.CompletedCount ?? 0;

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder containing videos" };
        if (dialog.ShowDialog(this) == true)
        {
            Processing.Folder = dialog.FolderName;
        }
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => Processing.ScanCommand.Execute(null);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Processing.CancelScan();
}
