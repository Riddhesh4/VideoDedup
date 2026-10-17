using System.Text.Json;
using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Application.Tests;

public sealed class MaintenanceServiceTests
{
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset When = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class TestClock : IQueueClock
    {
        public DateTimeOffset UtcNow { get; set; } = When;
    }

    private sealed class FakeStore : IAppDataStore
    {
        public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "video-dedup-maintenance", "data");

        public List<AppDataEntry> Entries { get; } = new();

        public List<string> Deleted { get; } = new();

        public Dictionary<string, string> Written { get; } = new();

        public Func<string, Exception?> Failure { get; set; } = _ => null;

        public int EmptyDirectoryPasses { get; private set; }

        public TimeSpan DeleteDelay { get; set; }

        public AppDataEntry Add(string relative, AppDataCategory category, long size, bool reparse = false)
        {
            var entry = new AppDataEntry(Path.Combine(DataRoot, relative), category, size, reparse);
            Entries.Add(entry);
            return entry;
        }

        public IReadOnlyList<AppDataEntry> Enumerate() => Entries.ToArray();

        public void DeleteFile(string path)
        {
            if (Failure(path) is { } failure)
            {
                throw failure;
            }

            Deleted.Add(path);
        }

        public void DeleteEmptyDirectories() => EmptyDirectoryPasses++;

        public void WriteText(string destinationPath, string content) => Written[destinationPath] = content;

        public bool FileExists(string path) => Entries.Any(entry => entry.FullPath == path);
    }

    private sealed class FakeIndex : IAppIndexSnapshotSource
    {
        public IReadOnlyList<string> Roots { get; set; } = [@"C:\Users\bob\Videos"];

        public AppIndexSnapshot Snapshot { get; set; } = new([], [], [], [], [], [], [], []);

        public Task<IReadOnlyList<string>> GetScanRootPathsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Roots);

        public Task<AppIndexSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }

    private static AppIndexSnapshot PopulatedSnapshot() => new(
        [ScanRoot.Create(Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "Videos", @"C:\Users\bob\Videos", ScanRootType.LocalFolder)],
        [new IndexedFile(FileA, Guid.Parse("cccccccc-0000-0000-0000-000000000003"), @"sub\private clip.mkv", 1024, When, ScanState.Hashed, Sha256: new string('a', 64))],
        [],
        [],
        [],
        [MatchPersistenceRecord.Create(FileA, FileB, MatchType.Exact, 1m, new EvidenceVersions(null, null),
            FingerprintVersion.Create(1, 0), ["SHA-256 content hashes are identical."])],
        [ReviewDecisionRecord.Create($"{FileA:D}|{FileB:D}", FileA, ReviewDecision.Keep, When)],
        [QuarantineRecord.Create(Guid.Parse("dddddddd-0000-0000-0000-000000000004"), FileB,
            @"C:\Users\bob\Videos\sub\copy.mkv", @"C:\Users\bob\AppData\Local\VideoDedup\Quarantine\copy.mkv",
            QuarantineState.Quarantined, false, When)]);

    private static (MaintenanceService Service, FakeStore Store, FakeIndex Index, IJobQueue Queue) Build(
        QueueStats? stats = null, ProcessingMetrics? metrics = null, IEnumerable<string>? notes = null)
    {
        var store = new FakeStore();
        var index = new FakeIndex();
        var queue = Substitute.For<IJobQueue>();
        queue.GetStatsAsync(Arg.Any<CancellationToken>()).Returns(stats ?? new QueueStats(0, 0, 0, 0));
        return (new MaintenanceService(store, index, queue, NullProcessingLog.Instance, metrics, notes, new TestClock()),
            store, index, queue);
    }

    [Fact]
    public async Task The_report_groups_areas_and_separates_reclaimable_from_kept_data()
    {
        var (service, store, _, _) = Build(notes: ["Hardware decoding is software-only."]);
        store.Add("videodedup.db", AppDataCategory.Index, 2048);
        store.Add(@"Discovery\files.json", AppDataCategory.DiscoveryIndex, 100);
        store.Add(@"logs\processing.jsonl", AppDataCategory.Logs, 50);
        store.Add("scan-roots.json", AppDataCategory.Configuration, 20);
        store.Add(@"Quarantine\copy.mkv", AppDataCategory.QuarantineStore, 4096);

        var report = await service.GetReportAsync(CancellationToken.None);

        report.HasReclaimableData.Should().BeTrue();
        report.ReclaimableBytes.Should().Be(2198);
        report.ReclaimableFileCount.Should().Be(3);
        report.QuarantineFileCount.Should().Be(1);
        report.QuarantineBytes.Should().Be(4096);
        report.TotalBytes.Should().Be(6314);
        report.Areas.Select(area => area.Area).Should().Contain([AppDataArea.IndexDatabase, AppDataArea.QuarantineStore]);
        report.Areas.Should().NotContain(area => area.Area == AppDataArea.QuarantineStore && area.ReclaimableFileCount > 0);
        report.Notes.Should().Contain(note => note.Contains("quarantined", StringComparison.OrdinalIgnoreCase));
        report.Notes.Should().Contain("Hardware decoding is software-only.");
    }

    [Fact]
    public async Task The_report_mentions_recent_throughput_only_when_work_happened()
    {
        var metrics = new ProcessingMetrics(new TestClock());
        var (service, store, _, _) = Build(metrics: metrics);
        store.Add("videodedup.db", AppDataCategory.Index, 10);

        (await service.GetReportAsync(CancellationToken.None)).Notes.Should()
            .NotContain(note => note.Contains("job(s) in the last", StringComparison.Ordinal));

        metrics.Write(ProcessingLogEvent.Create("Probe", ProcessingOutcome.Processed, TimeSpan.FromMilliseconds(5), clock: new TestClock()));
        (await service.GetReportAsync(CancellationToken.None)).Notes.Should()
            .Contain(note => note.Contains("job(s) in the last", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_writes_the_document_and_reports_what_it_contains()
    {
        var (service, store, index, _) = Build();
        index.Snapshot = PopulatedSnapshot();
        var destination = Path.Combine(Path.GetTempPath(), "video-dedup-maintenance", "export.json");

        var result = await service.ExportIndexAsync(new IndexExportRequest(destination, IncludePaths: true), CancellationToken.None);

        result.FileCount.Should().Be(1);
        result.MatchCount.Should().Be(1);
        result.RootCount.Should().Be(1);
        result.QuarantineRecordCount.Should().Be(1);
        result.IncludedPaths.Should().BeTrue();
        result.SizeBytes.Should().BeGreaterThan(0);
        store.Written.Should().ContainKey(destination);
        using var parsed = JsonDocument.Parse(store.Written[destination]);
        parsed.RootElement.GetProperty("roots")[0].GetProperty("path").GetString().Should().Be(index.Snapshot.Roots[0].Path);
        parsed.RootElement.GetProperty("files")[0].GetProperty("path").GetString().Should().Be(index.Snapshot.Files[0].RelativePath);
        parsed.RootElement.GetProperty("quarantine")[0].GetProperty("state").GetString().Should().Be("Quarantined");
    }

    [Fact]
    public async Task Export_without_paths_replaces_every_location_with_a_redaction_marker()
    {
        var (service, store, index, _) = Build();
        index.Snapshot = PopulatedSnapshot();
        var destination = Path.Combine(Path.GetTempPath(), "video-dedup-maintenance", "redacted.json");

        var result = await service.ExportIndexAsync(new IndexExportRequest(destination, IncludePaths: false), CancellationToken.None);

        result.IncludedPaths.Should().BeFalse();
        var document = store.Written[destination];
        document.Should().NotContain("bob");
        document.Should().NotContain("private clip.mkv");
        using var parsed = JsonDocument.Parse(document);
        parsed.RootElement.GetProperty("includesPaths").GetBoolean().Should().BeFalse();
        parsed.RootElement.GetProperty("roots")[0].GetProperty("path").ValueKind.Should().Be(JsonValueKind.Null);
        parsed.RootElement.GetProperty("files")[0].GetProperty("path").ValueKind.Should().Be(JsonValueKind.Null);
        parsed.RootElement.GetProperty("files")[0].GetProperty("pathRedacted").GetBoolean().Should().BeTrue();
        parsed.RootElement.GetProperty("files")[0].GetProperty("sha256").GetString().Should().Be(new string('a', 64),
            "hashes keep a redacted export useful for comparing runs");
        parsed.RootElement.GetProperty("matches")[0].GetProperty("matchType").GetString().Should().Be("Exact");
        parsed.RootElement.GetProperty("decisions")[0].GetProperty("decision").GetString().Should().Be("Keep");
        parsed.RootElement.GetProperty("quarantine")[0].GetProperty("destinationPathRedacted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Export_refuses_a_relative_path_or_one_inside_the_application_data_folder()
    {
        var (service, _, _, _) = Build();

        await FluentActions.Awaiting(() => service.ExportIndexAsync(new IndexExportRequest("relative.json", true), CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => service.ExportIndexAsync(
                new IndexExportRequest(Path.Combine(Path.GetTempPath(), "video-dedup-maintenance", "data", "export.json"), true),
                CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Deleting_the_index_needs_confirmation_and_removes_nothing_without_it()
    {
        var (service, store, _, _) = Build();
        store.Add("videodedup.db", AppDataCategory.Index, 10);

        var result = await service.DeleteIndexAsync(confirmed: false, CancellationToken.None);

        result.Confirmed.Should().BeFalse();
        result.Deleted.Should().BeEmpty();
        store.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_the_index_is_refused_while_work_is_queued_or_leased()
    {
        var (service, store, _, _) = Build(stats: new QueueStats(Queued: 4, Leased: 0, Completed: 0, DeadLetter: 0));
        store.Add("videodedup.db", AppDataCategory.Index, 10);

        await FluentActions.Awaiting(() => service.DeleteIndexAsync(confirmed: true, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        store.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_the_index_removes_rebuildable_data_and_keeps_quarantine_settings_and_the_root_folder()
    {
        var (service, store, _, _) = Build();
        store.Add("videodedup.db", AppDataCategory.Index, 10);
        store.Add(@"logs\processing.jsonl", AppDataCategory.Logs, 10);
        store.Add(@"Quarantine\copy.mkv", AppDataCategory.QuarantineStore, 10);
        store.Add("scan-roots.json", AppDataCategory.Configuration, 10);
        store.Add("mystery.bin", AppDataCategory.Unknown, 10);
        store.Add(Path.Combine("..", "outside.txt"), AppDataCategory.Index, 10);

        var result = await service.DeleteIndexAsync(confirmed: true, CancellationToken.None);

        result.Confirmed.Should().BeTrue();
        result.Deleted.Should().HaveCount(2);
        store.Deleted.Should().NotContain(path => path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
        store.Deleted.Should().NotContain(path => path.EndsWith("scan-roots.json", StringComparison.Ordinal));
        store.Deleted.Should().NotContain(path => path.Contains("outside", StringComparison.Ordinal));
        result.Retained.Should().HaveCount(4);
        result.Retained.Should().Contain(decision => decision.Reason == AppDataRetentionPolicy.QuarantineRetainedReason);
        result.Failures.Should().BeEmpty();
        store.EmptyDirectoryPasses.Should().Be(1, "folders emptied by the delete are tidied up");
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_is_reported_without_hiding_the_rest()
    {
        var (service, store, _, _) = Build();
        store.Add("videodedup.db", AppDataCategory.Index, 10);
        store.Add(@"logs\processing.jsonl", AppDataCategory.Logs, 10);
        store.Failure = path => path.EndsWith("videodedup.db", StringComparison.Ordinal)
            ? new UnauthorizedAccessException(@"Access to C:\Users\bob\AppData\Local\VideoDedup\videodedup.db is denied.")
            : null;

        var result = await service.DeleteIndexAsync(confirmed: true, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Deleted.Should().HaveCount(1);
        result.Failures.Should().HaveCount(1);
        result.Failures[0].Should().Contain("<path>", "the failure text must not leak the user's profile path");
        result.Failures[0].Should().NotContain("bob");
        store.EmptyDirectoryPasses.Should().Be(1);
    }
}
