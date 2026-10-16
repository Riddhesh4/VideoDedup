using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VideoDedup.Application;
using VideoDedup.Desktop;
using VideoDedup.Infrastructure;
using VideoDedup.Workers;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoDedup");
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == "--data") data = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(data);
        var tools = Path.Combine(AppContext.BaseDirectory, "media");
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == "--tools") tools = Path.GetFullPath(args[index + 1]);
        var probePath = Path.Combine(tools, "ffprobe.exe");
        var runner = new ProcessRunner([probePath]);
        using var queue = new SqliteJobQueue(Path.Combine(data, "videodedup.db"));
        queue.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var results = new SqliteMediaProcessingRepository(Path.Combine(data, "videodedup.db"));
        var roots = new DirectoryScanRootRepository(data);
        var discovery = new DirectoryDiscoveryRepository(Path.Combine(data, "Discovery"));
        var probe = new ProbeWorker(queue, results, new FfprobeMediaProbe(runner, probePath, new DecoderInventory(runner, probePath)));
        var hash = new HashWorker(queue, results, new Sha256ContentHasher());
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService>(s => new ProcessingService(probe, s.GetRequiredService<ILogger<ProcessingService>>()));
        builder.Services.AddSingleton<IHostedService>(s => new ProcessingService(hash, s.GetRequiredService<ILogger<ProcessingService>>()));
        using var host = builder.Build();
        host.StartAsync().GetAwaiter().GetResult();
        var app = new App();
        try
        {
            app.Run(new MainWindow(new ProcessingDashboard(roots, new ScanOrchestrator(new FileSystemDiscoverySource(), discovery),
                discovery, results, queue, new WorkFileAccess())));
        }
        finally { host.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
    }
}
