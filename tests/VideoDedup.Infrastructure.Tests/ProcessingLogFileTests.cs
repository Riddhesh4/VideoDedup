using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VideoDedup.Application;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class ProcessingLogFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "video-dedup-log-" + Guid.NewGuid().ToString("N"));

    public ProcessingLogFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ProcessingLogEvent Event(string worker, ProcessingOutcome outcome, long milliseconds, string? failure = null) =>
        ProcessingLogEvent.Create(worker, outcome, TimeSpan.FromMilliseconds(milliseconds), failure);

    [Fact]
    public void Every_line_is_structured_and_never_carries_a_path()
    {
        var log = new JsonlProcessingLog(_root);
        log.Write(Event("Probe", ProcessingOutcome.Processed, 120));
        log.Write(Event("Hash", ProcessingOutcome.Failed, 30, @"MediaProcessingException: AccessDenied C:\Users\bob\secret\x.mkv"));

        var lines = File.ReadAllLines(log.CurrentPath);
        lines.Should().HaveCount(2);
        lines.Should().NotContain(line => line.Contains("bob", StringComparison.OrdinalIgnoreCase));
        using var first = JsonDocument.Parse(lines[0]);
        first.RootElement.GetProperty("worker").GetString().Should().Be("Probe");
        first.RootElement.GetProperty("outcome").GetString().Should().Be("Processed");
        first.RootElement.GetProperty("ms").GetInt64().Should().Be(120);
        using var second = JsonDocument.Parse(lines[1]);
        second.RootElement.GetProperty("outcome").GetString().Should().Be("Failed");
        second.RootElement.GetProperty("failure").GetString().Should().Contain("<path>");
        log.WriteFailures.Should().Be(0);
    }

    [Fact]
    public void The_log_rotates_once_it_reaches_its_size_limit_and_keeps_only_the_configured_number_of_files()
    {
        var log = new JsonlProcessingLog(_root, maxBytesPerFile: 4096, retainedFiles: 1);
        for (var index = 0; index < 60; index++)
        {
            log.Write(Event("Probe", ProcessingOutcome.Processed, index, new string('f', 100)));
        }

        new FileInfo(log.CurrentPath).Length.Should().BeLessThanOrEqualTo(4096);
        File.Exists(log.CurrentPath + ".1").Should().BeTrue();
        File.Exists(log.CurrentPath + ".2").Should().BeFalse();
        Directory.GetFiles(Path.Combine(_root, "logs")).Should().HaveCount(2);
    }

    [Fact]
    public void A_log_that_cannot_be_written_counts_the_failure_instead_of_stopping_the_worker()
    {
        File.WriteAllText(Path.Combine(_root, "logs"), "this file blocks the log directory");
        var log = new JsonlProcessingLog(_root);

        log.Write(Event("Probe", ProcessingOutcome.Processed, 1));
        log.Write(Event("Probe", ProcessingOutcome.Processed, 2));

        log.WriteFailures.Should().Be(2);
    }

    [Fact]
    public void The_log_rejects_unusable_limits_and_relative_folders()
    {
        FluentActions.Invoking(() => new JsonlProcessingLog("relative")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new JsonlProcessingLog(_root, maxBytesPerFile: 10)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new JsonlProcessingLog(_root, retainedFiles: 99)).Should().Throw<ArgumentOutOfRangeException>();
    }
}

public sealed class SqliteIntegrityGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "video-dedup-integrity-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public SqliteIntegrityGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_missing_database_is_reported_as_missing_and_is_not_created_by_the_check()
    {
        var database = Path.Combine(_root, "videodedup.db");

        var report = await new SqliteIntegrityGuard().VerifyAsync(database, Token);

        report.Health.Should().Be(DatabaseHealth.Missing);
        report.BackupPath.Should().BeNull();
        File.Exists(database).Should().BeFalse();
    }

    [Fact]
    public async Task A_real_database_initialized_by_the_queue_passes_the_check_untouched()
    {
        var database = Path.Combine(_root, "videodedup.db");
        using (var queue = new SqliteJobQueue(database))
        {
            await queue.InitializeAsync(Token);
        }

        var report = await new SqliteIntegrityGuard().VerifyAsync(database, Token);

        report.Health.Should().Be(DatabaseHealth.Healthy);
        report.BackupPath.Should().BeNull();
        File.Exists(database).Should().BeTrue();
    }

    [Fact]
    public async Task A_corrupt_database_is_kept_as_a_backup_and_a_fresh_one_starts_cleanly()
    {
        var database = Path.Combine(_root, "videodedup.db");
        File.WriteAllText(database, "this is not a database at all, it is text");

        var report = await new SqliteIntegrityGuard().VerifyAsync(database, Token);

        report.Health.Should().Be(DatabaseHealth.Recovered);
        report.BackupPath.Should().NotBeNull();
        File.Exists(report.BackupPath!).Should().BeTrue("the unusable database is kept, never deleted");
        File.ReadAllText(report.BackupPath!).Should().Contain("not a database");
        File.Exists(database).Should().BeFalse("the application starts from a clean index");

        using var queue = new SqliteJobQueue(database);
        await queue.InitializeAsync(Token);
        (await queue.GetStatsAsync(Token)).Queued.Should().Be(0);
    }

    [Fact]
    public async Task A_corrupt_database_never_leaves_a_stale_write_ahead_file_beside_the_fresh_index()
    {
        var database = Path.Combine(_root, "videodedup.db");
        File.WriteAllText(database, "not a database");
        File.WriteAllText(database + "-wal", "stale write-ahead data");

        var report = await new SqliteIntegrityGuard().VerifyAsync(database, Token);

        report.Health.Should().Be(DatabaseHealth.Recovered);
        // Measured: SQLite itself discards a stray write-ahead file for a file it cannot read as a database, and the
        // guard moves aside whatever is left. Either way a fresh index never inherits a stale sidecar.
        File.Exists(database + "-wal").Should().BeFalse();
        File.Exists(database + "-shm").Should().BeFalse();
        File.Exists(report.BackupPath!).Should().BeTrue();
    }

    [Fact]
    public async Task An_index_that_cannot_be_opened_or_moved_is_reported_instead_of_thrown()
    {
        // The field failure: a database left behind by an older, restrictive ACL could be neither opened nor renamed
        // by the account running the application, and the unhandled exception ended the process with no window.
        var database = Path.Combine(_root, "videodedup.db");
        File.WriteAllText(database, "not a database");
        using var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = await new SqliteIntegrityGuard().VerifyAsync(database, Token);

        report.Health.Should().Be(DatabaseHealth.Unreadable);
        report.BackupPath.Should().BeNull();
        File.Exists(database).Should().BeTrue("an unusable index is never deleted");
        report.Detail.Should().Contain("nothing was deleted");
        report.Detail.Should().NotContain(_root, "the failure text is shown to a user, so it names no folder");
    }

    [Fact]
    public async Task The_guard_rejects_a_relative_path()
    {
        await FluentActions.Awaiting(() => new SqliteIntegrityGuard().VerifyAsync("videodedup.db", Token))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void The_queue_can_reopen_a_database_that_the_guard_checked()
    {
        var database = Path.Combine(_root, "reopen.db");
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY);";
        command.ExecuteNonQuery();
        connection.Close();

        File.Exists(database).Should().BeTrue();
    }
}
