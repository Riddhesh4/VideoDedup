using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>Durable manual decisions (Keep/Reject/NeedsReview) keyed by duplicate group and file.</summary>
public sealed class SqliteReviewDecisionStore(string databasePath) : IReviewDecisionStore
{
    private readonly string _path = Path.GetFullPath(databasePath);
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
                            CREATE TABLE IF NOT EXISTS review_decisions (
                                GroupKey TEXT NOT NULL,
                                FileId TEXT NOT NULL,
                                Decision INTEGER NOT NULL,
                                RecordedUtc TEXT NOT NULL,
                                PRIMARY KEY (GroupKey, FileId)
                            );
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

    public async Task SaveAsync(ReviewDecisionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO review_decisions (GroupKey, FileId, Decision, RecordedUtc) VALUES (@group, @file, @decision, @recorded)
            ON CONFLICT(GroupKey, FileId) DO UPDATE SET Decision=excluded.Decision, RecordedUtc=excluded.RecordedUtc;
            """;
        command.Parameters.AddWithValue("@group", record.GroupKey);
        command.Parameters.AddWithValue("@file", record.FileId.ToString("D"));
        command.Parameters.AddWithValue("@decision", (int)record.Decision);
        command.Parameters.AddWithValue("@recorded", record.RecordedUtc.ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<ReviewDecisionRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT GroupKey, FileId, Decision, RecordedUtc FROM review_decisions ORDER BY GroupKey, FileId;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<ReviewDecisionRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                records.Add(ReviewDecisionRecord.Create(reader.GetString(0), Guid.Parse(reader.GetString(1)),
                    (ReviewDecision)reader.GetInt32(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
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
