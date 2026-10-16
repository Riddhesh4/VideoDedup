using Microsoft.Data.Sqlite;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;
using VideoDedup.Workers;

namespace VideoDedup.Infrastructure.Tests;

public sealed class VisualPersistenceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "VideoDedup.VisualStore", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Database { get { Directory.CreateDirectory(_temp); return Path.Combine(_temp, "index.db"); } }

    [Fact]
    public async Task Visual_schema_upgrades_existing_database_and_roundtrips_unsigned_hash_quality_and_versions()
    {
        using var queue = new SqliteJobQueue(Database); await queue.InitializeAsync(Token);
        var media = new SqliteMediaProcessingRepository(Database);
        var record = Record();
        await media.SaveAsync(new MediaProcessingRecord(record.FileId, record.Revision, Sha256: new string('a', 64)), Token);
        var repository = new SqliteVisualProcessingRepository(Database);
        await repository.SaveAsync(record, Token);
        var reopened = new SqliteVisualProcessingRepository(Database);
        var stored = await reopened.GetAsync(record.FileId, Token);
        stored.Should().BeEquivalentTo(record);
        stored!.IsCurrent(record.Revision, new()).Should().BeTrue();
        stored.IsCurrent(record.Revision with { FileIdentity = "replacement" }, new()).Should().BeFalse();
        stored.IsCurrent(record.Revision, new(TimeSpan.FromSeconds(1))).Should().BeFalse();
        (stored with { Version = FingerprintVersion.Create(2, 0) }).IsCurrent(record.Revision, new()).Should().BeFalse();
        (stored with { Algorithm = "legacy" }).IsCurrent(record.Revision, new()).Should().BeFalse();
        await repository.SaveAsync(record with { Frames = [], Failure = MediaFailure.InvalidData }, Token);
        (await reopened.GetAsync(record.FileId, Token))!.Frames.Should().BeEmpty();
        (await media.GetAsync(record.FileId, Token))!.Sha256.Should().Be(new string('a', 64), "visual failure must not erase exact evidence");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await FluentActions.Awaiting(() => repository.SaveAsync(record, cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        (await reopened.GetAsync(record.FileId, Token))!.Failure.Should().Be(MediaFailure.InvalidData);
        await repository.SaveAsync(record with { Revision = record.Revision with { SizeBytes = 20 } }, Token);
        (await reopened.GetAsync(record.FileId, Token))!.Revision.SizeBytes.Should().Be(20);
    }

    [Fact]
    public async Task Concurrent_initialization_and_replacement_never_expose_partial_frames()
    {
        using var queue = new SqliteJobQueue(Database); await queue.InitializeAsync(Token);
        var record = Record();
        var alternate = record with { Frames = [Frame(0, 1), Frame(1, 2)] };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            var repository = new SqliteVisualProcessingRepository(Database);
            await repository.SaveAsync(i % 2 == 0 ? record : alternate, Token);
            var current = await repository.GetAsync(record.FileId, Token);
            current!.Frames.Count.Should().BeOneOf(1, 2);
            current.Frames[0].PerceptualHash.Should().Be(current.Frames.Count == 1 ? ulong.MaxValue : 1UL);
        }, Token)));
    }

    [Fact]
    public async Task Failed_frame_insert_rolls_back_prior_result_and_corrupt_values_are_typed()
    {
        using var queue = new SqliteJobQueue(Database); await queue.InitializeAsync(Token);
        var repository = new SqliteVisualProcessingRepository(Database); var record = Record();
        await repository.SaveAsync(record, Token);
        await Sql("CREATE TRIGGER reject_frame BEFORE INSERT ON visual_frames BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await FluentActions.Awaiting(() => repository.SaveAsync(record with { Frames = [Frame(0, 1)] }, Token)).Should().ThrowAsync<SqliteException>();
        (await repository.GetAsync(record.FileId, Token)).Should().BeEquivalentTo(record);
        await Sql("DROP TRIGGER reject_frame; UPDATE visual_frames SET Hash='invalid';");
        await FluentActions.Awaiting(() => repository.GetAsync(record.FileId, Token)).Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.InvalidData);
    }

    [Fact]
    public async Task Invalid_or_excessive_records_are_rejected_before_writing()
    {
        var repository = new SqliteVisualProcessingRepository(Database); var record = Record();
        foreach (var invalid in new[] { record with { Frames = [] }, record with { MaxFrames = 121 }, record with { IntervalTicks = 0 }, record with { Failure = MediaFailure.Timeout },
            record with { Frames = Enumerable.Range(0, 121).Select(i => Frame(i, 1)).ToArray() }, record with { Frames = [Frame(1, 1), Frame(0, 1)] } })
            await FluentActions.Awaiting(() => repository.SaveAsync(invalid, Token)).Should().ThrowAsync<ArgumentException>();
    }
    private async Task Sql(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database}"); await connection.OpenAsync(Token);
        using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(Token);
    }
    private static VisualProcessingRecord Record() => new(Guid.NewGuid(), new FileRevision(10, DateTimeOffset.UnixEpoch, "identity"),
        VisualAlgorithm.Name, VisualAlgorithm.Version, TimeSpan.FromSeconds(10).Ticks, 60, [Frame(0, ulong.MaxValue)]);
    private static VisualFingerprint Frame(int seconds, ulong hash) => VisualFingerprint.Create(TimeSpan.FromSeconds(seconds), hash, 0.75m, VisualAlgorithm.Version);
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
