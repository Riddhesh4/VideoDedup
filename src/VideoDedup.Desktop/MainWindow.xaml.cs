using System.Windows;
using System.Windows.Threading;
using VideoDedup.Application;
namespace VideoDedup.Desktop;

public partial class MainWindow : Window
{
    private readonly IProcessingDashboard _dashboard;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource _scan = new();
    private bool _refreshing;
    public MainWindow(IProcessingDashboard dashboard)
    {
        InitializeComponent(); _dashboard = dashboard;
        _timer.Tick += Refresh_Click; Loaded += Refresh_Click;
        Closed += (_, _) => { _timer.Stop(); _scan.Cancel(); };
        _timer.Start();
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Folder.Text)) { Status.Text = "Enter a folder path."; return; }
        Scan.IsEnabled = false; _scan.Dispose(); _scan = new();
        try { var path = Folder.Text; await Task.Run(() => _dashboard.ScanAsync(path, _scan.Token)); }
        catch (OperationCanceledException) { Status.Text = "Scan canceled; queued work retained."; }
        catch (Exception exception) { Status.Text = $"Scan failed ({exception.GetType().Name})."; }
        finally { Scan.IsEnabled = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _scan.Cancel();
    private async void Refresh_Click(object? sender, EventArgs e)
    {
        if (_refreshing) return; _refreshing = true;
        try
        {
            Results.ItemsSource = await Task.Run(() => _dashboard.RefreshAsync(CancellationToken.None));
            var stats = await Task.Run(() => _dashboard.StatsAsync(CancellationToken.None));
            Status.Text = $"Queued: {stats.Queued}  Processing: {stats.Leased}  Completed jobs: {stats.Completed}  Failed jobs: {stats.DeadLetter}";
        }
        catch (Exception exception) { Status.Text = $"Refresh unavailable ({exception.GetType().Name})."; }
        finally { _refreshing = false; }
    }
}
