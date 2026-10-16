using Microsoft.Data.Sqlite;
using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class QuarantineStorageTests : IDisposable
{
    private static readonly DateTimeOffset Original = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "VideoDedup.Quarantine", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Source
    {
        get
        {
            Directory.CreateDirectory(Path.Combine(_root, "media"));
            var path = Path.Combine(_root, "media", "source.mkv");
            File.WriteAllText(path, "payload");
            File.SetLastWriteTimeUtc(path, Original.UtcDateTime);
            return path;
        }
    }

    private string Destination
    {
        get
        {
            var directory = Path.Combine(_root, "quarantine");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "moved.mkv");
        }
    }

    [Fact]
    public void A_same_volume_move_relocates_the_file_and_keeps_its_timestamp()
    {
        var source = Source;
        var destination = Destination;

        new QuarantineStorage().Move(source, destination);

        File.Exists(source).Should().BeFalse();
        File.ReadAllText(destination).Should().Be("payload");
        File.GetLastWriteTimeUtc(destination).Should().Be(Original.UtcDateTime);
    }

    [Fact]
    public void An_existing_destination_is_never_overwritten()
    {
        var source = Source;
        var destination = Destination;
        File.WriteAllText(destination, "keep me");

        FluentActions.Invoking(() => new QuarantineStorage().Move(source, destination))
            .Should().Throw<StorageException>()
            .Which.Failure.Should().Be(StorageFailure.Conflict);

        File.ReadAllText(destination).Should().Be("keep me");
        File.ReadAllText(source).Should().Be("payload");
    }

    [Fact]
    public void A_missing_source_is_reported_as_not_found()
    {
        var destination = Destination;

        FluentActions.Invoking(() => new QuarantineStorage().Move(Path.Combine(_root, "media", "gone.mkv"), destination))
            .Should().Throw<StorageException>()
            .Which.Failure.Should().Be(StorageFailure.NotFound);
    }

    [Fact]
    public void A_cross_volume_move_copies_verifies_then_removes_the_original()
    {
        var source = Source;
        var destination = Destination;
        // A resolver makes the two paths look like different volumes, which exercises the copy path on one disk.
        var storage = new QuarantineStorage(path => path.Contains("media", StringComparison.OrdinalIgnoreCase) ? "V1" : "V2");
        storage.GetVolumeRoot(source).Should().Be("V1");
        storage.GetVolumeRoot(destination).Should().Be("V2");

        storage.Move(source, destination);

        File.Exists(source).Should().BeFalse();
        File.ReadAllText(destination).Should().Be("payload");
        File.GetLastWriteTimeUtc(destination).Should().Be(Original.UtcDateTime);
        Directory.GetFiles(Path.GetDirectoryName(destination)!, "*videodedup-partial*").Should().BeEmpty();
    }

    [Fact]
    public void A_locked_source_fails_typed_and_leaves_no_partial_copies_behind()
    {
        var source = Source;
        var destination = Destination;
        var storage = new QuarantineStorage(path => path.Contains("media", StringComparison.OrdinalIgnoreCase) ? "V1" : "V2");

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            FluentActions.Invoking(() => storage.Move(source, destination)).Should().Throw<StorageException>();
        }

        File.Exists(source).Should().BeTrue();
        File.ReadAllText(source).Should().Be("payload");
        File.Exists(destination).Should().BeFalse();
        Directory.GetFiles(Path.GetDirectoryName(destination)!, "*videodedup-partial*").Should().BeEmpty();
    }

    [Fact]
    public void Inspection_reports_files_directories_and_missing_paths()
    {
        var source = Source;
        var storage = new QuarantineStorage();

        var file = storage.Inspect(source);
        file.Exists.Should().BeTrue();
        file.IsDirectory.Should().BeFalse();
        file.IsSymbolicLink.Should().BeFalse();
        file.SizeBytes.Should().Be(new FileInfo(source).Length);

        var directory = storage.Inspect(_root);
        directory.Exists.Should().BeTrue();
        directory.IsDirectory.Should().BeTrue();

        var missing = storage.Inspect(Path.Combine(_root, "nothing-here.mkv"));
        missing.Exists.Should().BeFalse();
        storage.DirectoryExists(Path.Combine(_root, "nothing-here")).Should().BeFalse();
    }

    [Fact]
    public void Directories_are_created_and_writability_is_reported_honestly()
    {
        var storage = new QuarantineStorage();
        var nested = Path.Combine(_root, "a", "b", "c");

        storage.EnsureDirectory(nested);

        Directory.Exists(nested).Should().BeTrue();
        storage.IsWritableDirectory(nested).Should().BeTrue();
        storage.IsWritableDirectory(_root).Should().BeTrue();
        storage.IsWritableDirectory(@"Z:\definitely\not\here").Should().BeFalse();
        storage.GetVolumeRoot(Path.Combine(_root, "a")).Should().Be(Path.GetPathRoot(Path.GetFullPath(_root)));
        storage.GetVolumeRoot("bad\0path").Should().BeNull();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

public sealed class QuarantineLogTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "VideoDedup.QuarantineLog", Guid.NewGuid().ToString("N"));

    private string Database { get { Directory.CreateDirectory(_temp); return Path.Combine(_temp, "index.db"); } }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task An_intent_is_durable_before_the_move_and_survives_a_new_instance()
    {
        var log = new SqliteQuarantineLog(Database);
        var record = await log.BeginAsync(FileA, @"C:\Videos\a.mkv", @"D:\VideoDedup Quarantine\a-aaaaaaaa.mkv", true, Token);

        record.State.Should().Be(QuarantineState.Pending);
        var reopened = (await new SqliteQuarantineLog(Database).GetAsync(Token)).Should().ContainSingle().Subject;
        reopened.RecordId.Should().Be(record.RecordId);
        reopened.CrossVolume.Should().BeTrue();
        reopened.SourcePath.Should().Be(@"C:\Videos\a.mkv");
        reopened.CompletedUtc.Should().BeNull();
    }

    [Fact]
    public async Task Transitions_are_state_checked_and_durable()
    {
        var log = new SqliteQuarantineLog(Database);
        var record = await log.BeginAsync(FileA, @"C:\Videos\a.mkv", @"D:\q\a.mkv", false, Token);

        await log.MarkQuarantinedAsync(record.RecordId, Token);
        var quarantined = (await log.GetAsync(Token)).Single();
        quarantined.State.Should().Be(QuarantineState.Quarantined);
        quarantined.CompletedUtc.Should().NotBeNull();

        await FluentActions.Awaiting(() => log.MarkQuarantinedAsync(record.RecordId, Token))
            .Should().ThrowAsync<InvalidOperationException>("a completed row cannot be quarantined twice");

        await log.MarkRestoredAsync(record.RecordId, Token);
        (await log.GetAsync(Token)).Single().State.Should().Be(QuarantineState.Restored);

        var failed = await log.BeginAsync(FileA, @"C:\Videos\b.mkv", @"D:\q\b.mkv", false, Token);
        await log.MarkFailedAsync(failed.RecordId, "Access to the file was denied.", Token);
        var row = (await log.GetAsync(Token)).Single(item => item.RecordId == failed.RecordId);
        row.State.Should().Be(QuarantineState.Failed);
        row.FailureReason.Should().Contain("denied");
    }

    [Fact]
    public async Task Corrupt_rows_surface_typed_invalid_data()
    {
        var log = new SqliteQuarantineLog(Database);
        await log.BeginAsync(FileA, @"C:\Videos\a.mkv", @"D:\q\a.mkv", false, Token);
        await using (var connection = new SqliteConnection("Data Source=" + Database))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE quarantine_log SET SourcePath='relative.mkv';";
            await command.ExecuteNonQueryAsync(Token);
        }

        await FluentActions.Awaiting(() => new SqliteQuarantineLog(Database).GetAsync(Token))
            .Should().ThrowAsync<MediaProcessingException>()
            .Where(exception => exception.Failure == MediaFailure.InvalidData);
    }

    [Fact]
    public async Task Review_decisions_upsert_and_round_trip()
    {
        var store = new SqliteReviewDecisionStore(Database);
        var groupKey = $"{FileA:D}|{Guid.NewGuid():D}";
        await store.SaveAsync(ReviewDecisionRecord.Create(groupKey, FileA, ReviewDecision.Reject,
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)), Token);
        await store.SaveAsync(ReviewDecisionRecord.Create(groupKey, FileA, ReviewDecision.Keep,
            new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero)), Token);

        var records = await new SqliteReviewDecisionStore(Database).GetAllAsync(Token);

        records.Should().ContainSingle();
        records[0].Decision.Should().Be(ReviewDecision.Keep);
        records[0].RecordedUtc.Should().Be(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Corrupt_review_decisions_surface_typed_invalid_data()
    {
        var store = new SqliteReviewDecisionStore(Database);
        var groupKey = $"{FileA:D}|{Guid.NewGuid():D}";
        await store.SaveAsync(ReviewDecisionRecord.Create(groupKey, FileA, ReviewDecision.NeedsReview, DateTimeOffset.UtcNow), Token);
        await using (var connection = new SqliteConnection("Data Source=" + Database))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE review_decisions SET Decision=99;";
            await command.ExecuteNonQueryAsync(Token);
        }

        await FluentActions.Awaiting(() => new SqliteReviewDecisionStore(Database).GetAllAsync(Token))
            .Should().ThrowAsync<MediaProcessingException>()
            .Where(exception => exception.Failure == MediaFailure.InvalidData);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}

public sealed class DiscoveryFileCatalogTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static readonly Guid RootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Modified = new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);

    private static ScanRoot Root() => ScanRoot.Create(RootId, "Videos", @"C:\Videos", ScanRootType.LocalFolder,
        enabled: true, includeSubfolders: true, driveDetails: DriveDetails.Create("volume", DriveKind.Local, "V:", 1_000_000));

    private static IndexedFile File(string relativePath = "a.mkv") =>
        new(FileId, RootId, relativePath, 123_456, Modified, ScanState.Discovered);

    private static (DiscoveryFileCatalog Catalog, IScanRootRepository Roots, IDiscoveryRepository Discovery, IMediaProcessingRepository Media) Build(
        MediaProcessingRecord? mediaRecord, IndexedFile? indexedFile = null)
    {
        var roots = Substitute.For<IScanRootRepository>();
        roots.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ScanRoot>>([Root()]));
        var discovery = Substitute.For<IDiscoveryRepository>();
        discovery.GetFilesAsync(RootId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IndexedFile>>([indexedFile ?? File()]));
        var media = Substitute.For<IMediaProcessingRepository>();
        media.GetAsync(FileId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(mediaRecord));
        return (new DiscoveryFileCatalog(roots, discovery, media), roots, discovery, media);
    }

    [Fact]
    public async Task Probed_metadata_is_joined_with_the_indexed_record()
    {
        var metadata = new MediaMetadata(120.5, 2, 1920, 1080, "h264", "aac", true, 30, "matroska", 0, "yuv420p", "bt709", 8);
        var (catalog, _, _, _) = Build(new MediaProcessingRecord(FileId, new FileRevision(123_456, Modified), metadata,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var entry = (await catalog.GetEntriesAsync(Token)).Should().ContainSingle().Subject;

        entry.RootLabel.Should().Be("Videos");
        entry.RootEnabled.Should().BeTrue();
        entry.FullPath.Should().Be(@"C:\Videos\a.mkv");
        entry.HasSafePath.Should().BeTrue();
        entry.Sha256.Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        entry.Duration.Should().Be(TimeSpan.FromSeconds(120.5));
        entry.Width.Should().Be(1920);
        entry.VideoCodec.Should().Be("h264");
        entry.Container.Should().Be("matroska");
        entry.Revision.SizeBytes.Should().Be(123_456);
    }

    [Fact]
    public async Task A_file_without_a_media_row_still_appears_with_discovery_values()
    {
        var (catalog, _, _, _) = Build(null, File("sub/b.mkv"));

        var entry = (await catalog.GetEntriesAsync(Token)).Should().ContainSingle().Subject;

        entry.Sha256.Should().BeNull();
        entry.Duration.Should().BeNull();
        entry.Width.Should().BeNull();
        entry.FullPath.Should().Be(@"C:\Videos\sub\b.mkv");
    }

    [Fact]
    public async Task An_absurd_duration_never_produces_an_invalid_timespan()
    {
        var metadata = new MediaMetadata(double.MaxValue, 1, 2, 2, "h264", null, false, 1, "matroska", null, null, null, null);
        var (catalog, _, _, _) = Build(new MediaProcessingRecord(FileId, new FileRevision(123_456, Modified), metadata));

        var entry = (await catalog.GetEntriesAsync(Token)).Should().ContainSingle().Subject;

        entry.Duration.Should().BeNull();
    }
}
