using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>
/// Durable quarantine audit log in the shared SQLite database: one row per intended move, written before the file
/// is touched, so an interrupted batch is always explainable and undo survives process restarts.
/// </summary>
public sealed class SqliteQuarantineLog(string databasePath, IQueueClock? clock = null) : IQuarantineLog
{
    private readonly string _path = Path.GetFullPath(databasePath);
    private readonly IQueueClock _clock = clock ?? SystemQueueClock.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!_initialized)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!_initialized)
                    {
                        using var transaction = connection.BeginTransaction(deferred: false);
                        using var schema = connection.CreateCommand();
                        schema.Transaction = transaction;
                        schema.CommandText = """
                            CREATE TABLE IF NOT EXISTS quarantine_log (
                                RecordId TEXT PRIMARY KEY NOT NULL,
                                FileId TEXT NOT NULL,
                                SourcePath TEXT NOT NULL,
                                DestinationPath TEXT NOT NULL,
                                State INTEGER NOT NULL,
                                CrossVolume INTEGER NOT NULL,
                                CreatedUtc TEXT NOT NULL,
                                CompletedUtc TEXT NULL,
                                FailureReason TEXT NULL
                            );
                            CREATE INDEX IF NOT EXISTS ix_quarantine_log_file ON quarantine_log(FileId);
                            """;
                        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        transaction.Commit();
                        _initialized = true;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<QuarantineRecord> BeginAsync(Guid fileId, string sourcePath, string destinationPath,
        bool crossVolume, CancellationToken cancellationToken)
    {
        var created = _clock.UtcNow;
        var record = QuarantineRecord.Create(Guid.NewGuid(), fileId, sourcePath, destinationPath,
            QuarantineState.Pending, crossVolume, created);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quarantine_log (RecordId, FileId, SourcePath, DestinationPath, State, CrossVolume, CreatedUtc, CompletedUtc, FailureReason)
            VALUES (@id, @file, @source, @destination, @state, @cross, @created, NULL, NULL);
            """;
        command.Parameters.AddWithValue("@id", record.RecordId.ToString("D"));
        command.Parameters.AddWithValue("@file", record.FileId.ToString("D"));
        command.Parameters.AddWithValue("@source", record.SourcePath);
        command.Parameters.AddWithValue("@destination", record.DestinationPath);
        command.Parameters.AddWithValue("@state", (int)record.State);
        command.Parameters.AddWithValue("@cross", record.CrossVolume ? 1 : 0);
        command.Parameters.AddWithValue("@created", record.CreatedUtc.ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return record;
    }

    public Task MarkQuarantinedAsync(Guid recordId, CancellationToken cancellationToken) =>
        TransitionAsync(recordId, expected: QuarantineState.Pending, target: QuarantineState.Quarantined, failureReason: null, cancellationToken);

    public Task MarkFailedAsync(Guid recordId, string reason, CancellationToken cancellationToken) =>
        TransitionAsync(recordId, expected: QuarantineState.Pending, target: QuarantineState.Failed, reason, cancellationToken);

    public Task MarkRestoredAsync(Guid recordId, CancellationToken cancellationToken) =>
        TransitionAsync(recordId, expected: QuarantineState.Quarantined, target: QuarantineState.Restored, failureReason: null, cancellationToken);

    private async Task TransitionAsync(Guid recordId, QuarantineState expected, QuarantineState target, string? failureReason,
        CancellationToken cancellationToken)
    {
        var completed = _clock.UtcNow;
        var reason = failureReason?.Trim();
        if (reason is not null && reason.Length > 400)
        {
            reason = reason[..400];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE quarantine_log SET State=@state, CompletedUtc=@completed, FailureReason=@reason
            WHERE RecordId=@id AND State=@expected;
            """;
        command.Parameters.AddWithValue("@id", recordId.ToString("D"));
        command.Parameters.AddWithValue("@state", (int)target);
        command.Parameters.AddWithValue("@expected", (int)expected);
        command.Parameters.AddWithValue("@completed", completed.ToString("o"));
        command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            transaction.Rollback();
            throw new InvalidOperationException($"Quarantine entry '{recordId:D}' is not in the expected {expected} state.");
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<QuarantineRecord>> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RecordId, FileId, SourcePath, DestinationPath, State, CrossVolume, CreatedUtc, CompletedUtc, FailureReason
            FROM quarantine_log ORDER BY CreatedUtc, RecordId;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<QuarantineRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                records.Add(QuarantineRecord.Create(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    (QuarantineState)reader.GetInt32(4),
                    reader.GetInt32(5) != 0,
                    DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException
                or InvalidCastException)
            {
                throw new MediaProcessingException(MediaFailure.InvalidData);
            }
        }

        return records;
    }
}
