using Microsoft.Data.Sqlite;
using VideoDedup.Application;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class SqliteJobQueueTests : IDisposable
{
    private static readonly SqliteJobQueueOptions ShortRetry = new(TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VideoDedup.Queue.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Enqueue_claim_complete_roundtrip_updates_stats()
    {
        var clock = new ManualQueueClock(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        using var queue = await OpenAsync(clock);
        var fileId = Guid.NewGuid();

        var enqueued = await queue.EnqueueAsync(fileId, JobType.Probe, Payload(), null, TestContext.Current.CancellationToken);
        enqueued.State.Should().Be(JobState.Queued);

        var claimed = await queue.ClaimAsync(new QueueClaimRequest("worker-1", TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken);
        claimed.Should().ContainSingle().Which.Id.Should().Be(enqueued.Id);
        claimed.Single().State.Should().Be(JobState.Leased);
        claimed.Single().LeaseOwner.Should().Be("worker-1");

        await queue.CompleteAsync(enqueued.Id, "worker-1", TestContext.Current.CancellationToken);
        (await queue.GetStatsAsync(TestContext.Current.CancellationToken)).Should().Be(new QueueStats(0, 0, 1, 0));
    }

    [Fact]
    public async Task Queued_jobs_survive_reopen_and_completed_work_is_not_repeated()
    {
        var clock = new ManualQueueClock(DateTimeOffset.UtcNow);
        var db = DbPath();
        using (var queue = await OpenAsync(clock, db))
        {
            var job = await queue.EnqueueAsync(Guid.NewGuid(), JobType.Hash, Payload(), null, TestContext.Current.CancellationToken);
            var claimed = await queue.ClaimAsync(new QueueClaimRequest("w", TimeSpan.FromMinutes(5), MaxJobs: 1), TestContext.Current.CancellationToken);
            await queue.CompleteAsync(job.Id, "w", TestContext.Current.CancellationToken);
            claimed.Should().ContainSingle();
            await queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), null, TestContext.Current.CancellationToken);
        }

        using (var reopened = await OpenAsync(clock, db))
        {
            var stats = await reopened.GetStatsAsync(TestContext.Current.CancellationToken);
            stats.Should().Be(new QueueStats(1, 0, 1, 0));
            var claimed = await reopened.ClaimAsync(new QueueClaimRequest("w2", TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken);
            claimed.Should().ContainSingle().Which.JobType.Should().Be(JobType.Probe);
        }
    }

    [Fact]
    public async Task Concurrent_claims_never_duplicate_a_job()
    {
        using var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow));
        await queue.EnqueueAsync(Guid.NewGuid(), JobType.Match, Payload(), null, TestContext.Current.CancellationToken);

        var winners = await Task.WhenAll(Enumerable.Range(0, 10).Select(index =>
            Task.Run(() => queue.ClaimAsync(new QueueClaimRequest("worker-" + index, TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken))));

        winners.Count(result => result.Count == 1).Should().Be(1);
        winners.SelectMany(result => result).Should().ContainSingle();

        for (var index = 0; index < 20; index++)
            await queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), null, TestContext.Current.CancellationToken);
        var batches = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            Task.Run(() => queue.ClaimAsync(new QueueClaimRequest("drain-" + index, TimeSpan.FromMinutes(5), MaxJobs: 10), TestContext.Current.CancellationToken))));
        var ids = batches.SelectMany(batch => batch).Select(job => job.Id).ToArray();
        ids.Should().HaveCount(20);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Duplicate_active_enqueue_returns_existing_without_new_row()
    {
        using var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow));
        var fileId = Guid.NewGuid();

        var first = await queue.EnqueueAsync(fileId, JobType.Probe, Payload(), null, TestContext.Current.CancellationToken);
        var second = await queue.EnqueueAsync(fileId, JobType.Probe, Payload(), null, TestContext.Current.CancellationToken);

        second.Id.Should().Be(first.Id);
        (await queue.GetStatsAsync(TestContext.Current.CancellationToken)).Queued.Should().Be(1);

        var otherType = await queue.EnqueueAsync(fileId, JobType.Hash, Payload(), null, TestContext.Current.CancellationToken);
        otherType.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task Batch_is_atomic_when_any_item_is_invalid()
    {
        using var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow));

        await FluentActions.Awaiting(() => queue.EnqueueBatchAsync(
            new[]
            {
                new QueueEnqueueItem(Guid.NewGuid(), JobType.Probe, Payload()),
                new QueueEnqueueItem(Guid.NewGuid(), JobType.Probe, "broken"),
            }, TestContext.Current.CancellationToken)).Should().ThrowAsync<ArgumentException>();

        (await queue.GetStatsAsync(TestContext.Current.CancellationToken)).Should().Be(new QueueStats(0, 0, 0, 0));
    }

    [Fact]
    public async Task Fail_retries_with_backoff_then_poison_to_dead_letter()
    {
        var clock = new ManualQueueClock(DateTimeOffset.Parse("2026-03-04T05:06:07Z"));
        using var queue = await OpenAsync(clock, options: new SqliteJobQueueOptions(TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)));
        var job = await queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), new EnqueueOptions(MaxAttempts: 2), TestContext.Current.CancellationToken);
        var claim = new QueueClaimRequest("w", TimeSpan.FromMinutes(5), MaxJobs: 5);

        var leased = (await queue.ClaimAsync(claim, TestContext.Current.CancellationToken)).Single();
        await queue.FailAsync(leased.Id, "w", "ffprobe crashed", TestContext.Current.CancellationToken);

        (await queue.ClaimAsync(claim, TestContext.Current.CancellationToken)).Should().BeEmpty();

        clock.Advance(TimeSpan.FromMinutes(15));
        var retried = (await queue.ClaimAsync(claim, TestContext.Current.CancellationToken)).Should().ContainSingle().Subject;
        retried.Attempts.Should().Be(1);
        retried.Id.Should().Be(job.Id);

        await queue.FailAsync(retried.Id, "w", "still crashing", TestContext.Current.CancellationToken);
        (await queue.ClaimAsync(claim, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await queue.GetStatsAsync(TestContext.Current.CancellationToken)).Should().Be(new QueueStats(0, 0, 0, 1));
    }

    [Fact]
    public async Task Stale_lease_is_recovered_for_reclaim()
    {
        var clock = new ManualQueueClock(DateTimeOffset.UtcNow);
        using var queue = await OpenAsync(clock);
        var job = await queue.EnqueueAsync(Guid.NewGuid(), JobType.Hash, Payload(), null, TestContext.Current.CancellationToken);

        (await queue.ClaimAsync(new QueueClaimRequest("w1", TimeSpan.FromMinutes(1)), TestContext.Current.CancellationToken))
            .Should().ContainSingle();
        (await queue.ClaimAsync(new QueueClaimRequest("w2", TimeSpan.FromMinutes(1)), TestContext.Current.CancellationToken))
            .Should().BeEmpty();

        clock.Advance(TimeSpan.FromMinutes(2));
        var recovered = (await queue.ClaimAsync(new QueueClaimRequest("w2", TimeSpan.FromMinutes(1)), TestContext.Current.CancellationToken))
            .Should().ContainSingle().Subject;
        recovered.Id.Should().Be(job.Id);
        recovered.State.Should().Be(JobState.Leased);
    }

    [Fact]
    public async Task Corrupt_stored_payloads_are_quarantined_and_never_returned()
    {
        var db = DbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        await using (var setup = new SqliteConnection("Data Source=" + db))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using (var pragma = setup.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL;";
                await pragma.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        using (var queue = await OpenAsync(SystemQueueClock.Instance, db))
        {
            InsertRaw(db, Guid.NewGuid(), JobType.Probe, new string('x', QueuePayloadValidator.MaxPayloadChars + 1));
            InsertRaw(db, Guid.NewGuid(), JobType.Probe, "{oops");
            await queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), null, TestContext.Current.CancellationToken);

            var claimed = await queue.ClaimAsync(new QueueClaimRequest("w", TimeSpan.FromMinutes(5), MaxJobs: 10), TestContext.Current.CancellationToken);
            claimed.Should().ContainSingle().Which.PayloadJson.Should().Be(Payload());
            (await queue.GetStatsAsync(TestContext.Current.CancellationToken)).Should().Be(new QueueStats(0, 1, 0, 2));
        }
    }

    [Fact]
    public async Task Migration_from_empty_creates_versioned_schema()
    {
        var db = DbPath();
        using (var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow), db))
        {
            await queue.EnqueueAsync(Guid.NewGuid(), JobType.Discover, Payload(), null, TestContext.Current.CancellationToken);
        }

        await using var connection = new SqliteConnection("Data Source=" + db);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT Version FROM schema_version;";
        (await version.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be((long)1);
        await using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        (await mode.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().BeEquivalentTo("wal");
    }

    [Fact]
    public async Task Migration_from_prior_schema_preserves_rows()
    {
        var db = DbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        var legacyId = Guid.NewGuid();
        await using (var setup = new SqliteConnection("Data Source=" + db))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using (var create = setup.CreateCommand())
            {
                create.CommandText = "CREATE TABLE jobs (Id TEXT NOT NULL PRIMARY KEY, FileId TEXT NOT NULL, JobType INTEGER NOT NULL, State INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, NextVisibleUtc TEXT NOT NULL, PayloadJson TEXT NOT NULL);";
                await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using (var insert = setup.CreateCommand())
            {
                insert.CommandText = "INSERT INTO jobs (Id, FileId, JobType, State, CreatedUtc, NextVisibleUtc, PayloadJson) VALUES (@id, @file, 1, 0, @now, @now, @payload);";
                insert.Parameters.AddWithValue("@id", legacyId.ToString("D"));
                insert.Parameters.AddWithValue("@file", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
                insert.Parameters.AddWithValue("@payload", Payload());
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        using (var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow), db))
        {
            var claimed = await queue.ClaimAsync(new QueueClaimRequest("w", TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken);
            claimed.Should().ContainSingle().Which.Id.Should().Be(legacyId);
        }
    }

    [Fact]
    public async Task Cancellation_is_honored_at_every_transition()
    {
        using var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await FluentActions.Awaiting(() => queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), null, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => queue.ClaimAsync(new QueueClaimRequest("w", TimeSpan.FromMinutes(1)), cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => queue.CompleteAsync(Guid.NewGuid(), "w", cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => queue.FailAsync(Guid.NewGuid(), "w", "boom", cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => queue.GetStatsAsync(cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => queue.InitializeAsync(cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Per_drive_and_global_limits_are_respected()
    {
        using var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow));
        for (var index = 0; index < 3; index++)
        {
            await queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), new EnqueueOptions(DriveId: "drive-A"), TestContext.Current.CancellationToken);
            await queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), new EnqueueOptions(DriveId: "drive-B"), TestContext.Current.CancellationToken);
        }

        var perDrive = await queue.ClaimAsync(
            new QueueClaimRequest("w", TimeSpan.FromMinutes(5), MaxJobs: 10, MaxPerDrive: 1), TestContext.Current.CancellationToken);
        perDrive.Should().HaveCount(2);
        perDrive.Select(job => job.DriveId).Should().BeEquivalentTo("drive-A", "drive-B");

        foreach (var job in perDrive)
            await queue.CompleteAsync(job.Id, "w", TestContext.Current.CancellationToken);

        var global = await queue.ClaimAsync(
            new QueueClaimRequest("w", TimeSpan.FromMinutes(5), MaxJobs: 10, MaxGlobal: 2), TestContext.Current.CancellationToken);
        global.Should().HaveCount(2);
    }

    [Fact]
    public async Task Claim_succeeds_once_a_blocking_writer_releases_the_database()
    {
        var db = DbPath();
        using (var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow), db))
        {
            await queue.EnqueueAsync(Guid.NewGuid(), JobType.Match, Payload(), null, TestContext.Current.CancellationToken);
        }

        await using var blocker = new SqliteConnection("Data Source=" + db);
        await blocker.OpenAsync(TestContext.Current.CancellationToken);
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var init = Task.Run(() => OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow), db));
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        await using (var release = blocker.CreateCommand())
        {
            release.CommandText = "ROLLBACK;";
            await release.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var queue2 = await init;
        var claimed = await queue2.ClaimAsync(new QueueClaimRequest("w", TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken);
        claimed.Should().ContainSingle();
    }

    [Fact]
    public async Task Complete_and_fail_require_the_lease_owner()
    {
        using var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow));
        var job = await queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), null, TestContext.Current.CancellationToken);
        (await queue.ClaimAsync(new QueueClaimRequest("owner", TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken))
            .Should().ContainSingle();

        await FluentActions.Awaiting(() => queue.CompleteAsync(job.Id, "impostor", TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => queue.FailAsync(job.Id, "impostor", "x", TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => queue.CompleteAsync(Guid.NewGuid(), "owner", TestContext.Current.CancellationToken))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Enqueue_validation_rejects_bad_input()
    {
        using var queue = await OpenAsync(new ManualQueueClock(DateTimeOffset.UtcNow));

        await FluentActions.Awaiting(() => queue.EnqueueAsync(Guid.Empty, JobType.Probe, Payload(), null, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => queue.EnqueueAsync(Guid.NewGuid(), (JobType)999, Payload(), null, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, "broken", null, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => queue.EnqueueAsync(Guid.NewGuid(), JobType.Probe, Payload(), new EnqueueOptions(MaxAttempts: 0), TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static string Payload() => """{"version":1,"file":"clip.mp4"}""";

    private async Task<SqliteJobQueue> OpenAsync(IQueueClock clock, string? db = null, SqliteJobQueueOptions? options = null)
    {
        var queue = new SqliteJobQueue(db ?? DbPath(), clock, options ?? ShortRetry);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        return queue;
    }

    private string DbPath() => Path.Combine(_directory, Guid.NewGuid().ToString("N"), "queue.db");

    private static void InsertRaw(string db, Guid fileId, JobType type, string payload)
    {
        using var connection = new SqliteConnection("Data Source=" + db);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO jobs (Id, FileId, JobType, State, Priority, Attempts, MaxAttempts, CreatedUtc, NextVisibleUtc, PayloadJson) VALUES (@id, @file, @type, 0, 0, 0, 5, @now, @now, @payload);";
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@file", fileId.ToString("D"));
        command.Parameters.AddWithValue("@type", (int)type);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@payload", payload);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class ManualQueueClock(DateTimeOffset now) : IQueueClock
    {
        private DateTimeOffset _now = now;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
