using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VideoDedup.Application;
using VideoDedup.Desktop;
using VideoDedup.Desktop.ViewModels;
using VideoDedup.Domain;
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
        var audioTools = Path.Combine(AppContext.BaseDirectory, "audio");
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == "--tools") tools = Path.GetFullPath(args[index + 1]);
            if (args[index] == "--audio-tools") audioTools = Path.GetFullPath(args[index + 1]);
        }
        var database = Path.Combine(data, "videodedup.db");
        var probePath = Path.Combine(tools, "ffprobe.exe");
        var ffmpegPath = Path.Combine(tools, "ffmpeg.exe");
        var runner = new ProcessRunner([probePath, ffmpegPath]);
        using var queue = new SqliteJobQueue(database);
        queue.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var results = new SqliteMediaProcessingRepository(database);
        var roots = new DirectoryScanRootRepository(data);
        var discovery = new DirectoryDiscoveryRepository(Path.Combine(data, "Discovery"));
        var probe = new ProbeWorker(queue, results, new FfprobeMediaProbe(runner, probePath, new DecoderInventory(runner, probePath)));
        var hash = new HashWorker(queue, results, new Sha256ContentHasher());
        var visuals = new SqliteVisualProcessingRepository(database);
        var visual = new VisualFingerprintWorker(queue, results, visuals, new FfprobeMediaProbe(runner, probePath),
            new FfmpegVisualFingerprinter(runner, ffmpegPath));
        var audios = new SqliteAudioProcessingRepository(database);
        var audio = new AudioFingerprintWorker(queue, audios, results, new FfprobeMediaProbe(runner, probePath),
            new FpcalcAudioFingerprinter(runner, Path.Combine(audioTools, "chromaprint-fpcalc-1.6.2-windows-x86_64", "fpcalc.exe")));
        var matchResults = new SqliteMatchResultRepository(database);
        var matching = new MatchOrchestrator(new MatchEvidenceSource(discovery, roots, results, visuals, audios),
            matchResults, new DuplicateMatchPolicy(MatchPolicyOptions.Default));
        var match = new MatchWorker(queue, matching);

        // Review, quarantine, and the shared pause/resume gate. The UI only ever talks to Application ports.
        var control = new WorkerStatusRegistry([probe.Name, hash.Name, visual.Name, audio.Name, match.Name]);
        var storage = new QuarantineStorage();
        var catalog = new DiscoveryFileCatalog(roots, discovery, results);
        var quarantineLog = new SqliteQuarantineLog(database);
        var quarantine = new QuarantineService(catalog, storage, quarantineLog,
            new QuarantineServiceOptions(Path.Combine(data, "Quarantine")));
        var review = new ReviewService(matchResults, new SqliteReviewDecisionStore(database), catalog, quarantineLog, storage);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService>(s => new ProcessingService(probe, control, s.GetRequiredService<ILogger<ProcessingService>>()));
        builder.Services.AddSingleton<IHostedService>(s => new ProcessingService(hash, control, s.GetRequiredService<ILogger<ProcessingService>>()));
        builder.Services.AddSingleton<IHostedService>(s => new ProcessingService(visual, control, s.GetRequiredService<ILogger<ProcessingService>>()));
        builder.Services.AddSingleton<IHostedService>(s => new ProcessingService(audio, control, s.GetRequiredService<ILogger<ProcessingService>>()));
        builder.Services.AddSingleton<IHostedService>(s => new ProcessingService(match, control, s.GetRequiredService<ILogger<ProcessingService>>()));
        using var host = builder.Build();
        host.StartAsync().GetAwaiter().GetResult();
        var app = new App();
        var dashboard = new ProcessingDashboard(roots, new ScanOrchestrator(new FileSystemDiscoverySource(), discovery),
            discovery, results, queue, new WorkFileAccess(), visuals, audios: audios, control: control);
        try
        {
            app.Run(new MainWindow(new ProcessingViewModel(dashboard),
                new ReviewViewModel(review, quarantine, new MessageBoxConfirmationPrompt(),
                    new FfmpegFramePreviewSource(runner, ffmpegPath))));
        }
        finally { host.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
    }
}
