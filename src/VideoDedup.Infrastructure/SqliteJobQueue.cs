using Microsoft.Data.Sqlite;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>Single-writer tuning for the durable queue.</summary>
public sealed record SqliteJobQueueOptions(
    TimeSpan BaseRetryDelay,
    TimeSpan MaxRetryDelay,
    double JitterFactor = 0.2,
    int BusyTimeoutMs = 5000)
{
    public static SqliteJobQueueOptions Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));

    public void Validate()
    {
        if (BaseRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BaseRetryDelay));
        if (MaxRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay));
        if (JitterFactor is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(JitterFactor));
        if (BusyTimeoutMs < 1)
            throw new ArgumentOutOfRangeException(nameof(BusyTimeoutMs));
    }
}

/// <summary>
/// Durable SQLite job queue. Mutations serialize through an in-process single-writer gate plus
/// <c>BEGIN IMMEDIATE</c> transactions; every connection enables foreign keys and a busy timeout.
/// Claim/complete/fail each commit before returning, so workers never hold a database transaction
/// while processing media. Payloads are revalidated on every claim and poisoned rows go to
/// <see cref="JobState.DeadLetter"/> instead of reaching a worker.
/// </summary>
public sealed class SqliteJobQueue : IJobQueue, IDisposable
{
    private const int MaxErrorChars = 4000;
    private const int MaxClaimScan = 500;

    private readonly string _databasePath;
    private readonly IQueueClock _clock;
    private readonly SqliteJobQueueOptions _options;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private bool _disposed;

    public SqliteJobQueue(string? databasePath = null, IQueueClock? clock = null, SqliteJobQueueOptions? options = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoDedup", "videodedup.db")
            : databasePath;
        _clock = clock ?? SystemQueueClock.Instance;
        _options = options ?? SqliteJobQueueOptions.Default;
        _options.Validate();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", null, cancellationToken).ConfigureAwait(false);
            await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection,
                    "CREATE TABLE IF NOT EXISTS schema_version (Version INTEGER NOT NULL PRIMARY KEY, AppliedUtc TEXT NOT NULL);",
                    null, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection,
                    """
                    CREATE TABLE IF NOT EXISTS jobs (
                        Id TEXT NOT NULL PRIMARY KEY,
                        FileId TEXT NOT NULL,
                        JobType INTEGER NOT NULL,
                        State INTEGER NOT NULL,
                        Priority INTEGER NOT NULL DEFAULT 0,
                        Attempts INTEGER NOT NULL DEFAULT 0,
                        MaxAttempts INTEGER NOT NULL DEFAULT 5,
                        CreatedUtc TEXT NOT NULL,
                        NextVisibleUtc TEXT NOT NULL,
                        LeaseOwner TEXT NULL,
                        LeaseExpiresUtc TEXT NULL,
                        PayloadJson TEXT NOT NULL,
                        DriveId TEXT NULL,
                        LastError TEXT NULL
                    );
                    """, null, cancellationToken).ConfigureAwait(false);

                await EnsureColumnAsync(connection, "jobs", "Attempts", "ALTER TABLE jobs ADD COLUMN Attempts INTEGER NOT NULL DEFAULT 0;", cancellationToken).ConfigureAwait(false);
                await EnsureColumnAsync(connection, "jobs", "Priority", "ALTER TABLE jobs ADD COLUMN Priority INTEGER NOT NULL DEFAULT 0;", cancellationToken).ConfigureAwait(false);
                await EnsureColumnAsync(connection, "jobs", "MaxAttempts", "ALTER TABLE jobs ADD COLUMN MaxAttempts INTEGER NOT NULL DEFAULT 5;", cancellationToken).ConfigureAwait(false);
                await EnsureColumnAsync(connection, "jobs", "LeaseOwner", "ALTER TABLE jobs ADD COLUMN LeaseOwner TEXT NULL;", cancellationToken).ConfigureAwait(false);
                await EnsureColumnAsync(connection, "jobs", "LeaseExpiresUtc", "ALTER TABLE jobs ADD COLUMN LeaseExpiresUtc TEXT NULL;", cancellationToken).ConfigureAwait(false);
                await EnsureColumnAsync(connection, "jobs", "DriveId", "ALTER TABLE jobs ADD COLUMN DriveId TEXT NULL;", cancellationToken).ConfigureAwait(false);
                await EnsureColumnAsync(connection, "jobs", "LastError", "ALTER TABLE jobs ADD COLUMN LastError TEXT NULL;", cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(connection,
                    "CREATE INDEX IF NOT EXISTS ix_jobs_claim ON jobs (State, NextVisibleUtc, Priority DESC, CreatedUtc);",
                    null, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection,
                    "CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active ON jobs (FileId, JobType) WHERE State IN (0, 1);",
                    null, cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(connection,
                    "INSERT OR IGNORE INTO schema_version (Version, AppliedUtc) VALUES (1, @now);",
                    new Dictionary<string, object?> { ["@now"] = Format(_clock.UtcNow) }, cancellationToken).ConfigureAwait(false);
                await CommitAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<QueueJob> EnqueueAsync(Guid fileId, JobType jobType, string payloadJson, EnqueueOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = await EnqueueBatchAsync(new[] { new QueueEnqueueItem(fileId, jobType, payloadJson, options) }, cancellationToken).ConfigureAwait(false);
        return resolved[0];
    }

    public async Task<IReadOnlyList<QueueJob>> EnqueueBatchAsync(IReadOnlyList<QueueEnqueueItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new ArgumentException("Batch must contain at least one item.", nameof(items));
        if (items.Count > 500)
            throw new ArgumentException("Batch must contain at most 500 items.", nameof(items));
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.FileId == Guid.Empty)
                throw new ArgumentException("File ID must not be empty.", nameof(items));
            if (!Enum.IsDefined(item.JobType))
                throw new ArgumentOutOfRangeException(nameof(items), "Unknown job type.");
            QueuePayloadValidator.Validate(item.PayloadJson);
            item.Options?.Validate();
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
            var results = new List<QueueJob>(items.Count);
            try
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(await EnqueueOneAsync(connection, item, cancellationToken).ConfigureAwait(false));
                }

                await CommitAsync(connection, cancellationToken).ConfigureAwait(false);
                return results.ToArray();
            }
            catch
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<IReadOnlyList<QueueJob>> ClaimAsync(QueueClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _clock.UtcNow;
                var nowText = Format(now);

                await ExecuteAsync(connection,
                    "UPDATE jobs SET State = 0, LeaseOwner = NULL, LeaseExpiresUtc = NULL WHERE State = 1 AND LeaseExpiresUtc IS NOT NULL AND LeaseExpiresUtc <= @now;",
                    new Dictionary<string, object?> { ["@now"] = nowText }, cancellationToken).ConfigureAwait(false);

                var candidates = await ReadCandidatesAsync(connection, request, nowText, cancellationToken).ConfigureAwait(false);
                var leasedCounts = await ReadLeasedCountsAsync(connection, cancellationToken).ConfigureAwait(false);

                var claimed = new List<QueueJob>();
                var globalLeased = 0;
                foreach (var count in leasedCounts.Values)
                    globalLeased += count;
                var perDrive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var candidate in candidates)
                {
                    if (claimed.Count >= request.MaxJobs)
                        break;
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!IsPayloadUsable(candidate.PayloadJson))
                    {
                        await ExecuteAsync(connection,
                            "UPDATE jobs SET State = 3, LeaseOwner = NULL, LeaseExpiresUtc = NULL, LastError = @error WHERE Id = @id;",
                            new Dictionary<string, object?> { ["@id"] = candidate.Id.ToString("D"), ["@error"] = "Stored payload failed validation and was quarantined." },
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var driveKey = candidate.DriveId ?? string.Empty;
                    if (!perDrive.TryGetValue(driveKey, out var claimedForDrive))
                    {
                        claimedForDrive = 0;
                        perDrive[driveKey] = 0;
                    }

                    var leasedForDrive = leasedCounts.TryGetValue(driveKey, out var driveLeased) ? driveLeased : 0;
                    if (request.MaxGlobal is not null && globalLeased >= request.MaxGlobal)
                        break;
                    if (request.MaxPerDrive is not null && leasedForDrive + claimedForDrive >= request.MaxPerDrive)
                        continue;

                    var expires = now + request.LeaseDuration;
                    var updated = await ExecuteAsync(connection,
                        "UPDATE jobs SET State = 1, LeaseOwner = @owner, LeaseExpiresUtc = @expires WHERE Id = @id AND State = 0;",
                        new Dictionary<string, object?>
                        {
                            ["@owner"] = request.WorkerId,
                            ["@expires"] = Format(expires),
                            ["@id"] = candidate.Id.ToString("D"),
                        }, cancellationToken).ConfigureAwait(false);
                    if (updated == 0)
                        continue;

                    globalLeased++;
                    perDrive[driveKey] = claimedForDrive + 1;
                    claimed.Add(candidate with
                    {
                        State = JobState.Leased,
                        LeaseOwner = request.WorkerId,
                        LeaseExpiresUtc = expires,
                    });
                }

                await CommitAsync(connection, cancellationToken).ConfigureAwait(false);
                return claimed.ToArray();
            }
            catch
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID must not be empty.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Worker ID must not be blank.", nameof(workerId));
        cancellationToken.ThrowIfCancellationRequested();

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                var job = await ReadByIdAsync(connection, jobId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Job '{jobId}' was not found.");
                if (job.State != JobState.Leased || !string.Equals(job.LeaseOwner, workerId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Only the lease owner can complete a leased job.");

                await ExecuteAsync(connection,
                    "UPDATE jobs SET State = 2, LeaseOwner = NULL, LeaseExpiresUtc = NULL WHERE Id = @id;",
                    new Dictionary<string, object?> { ["@id"] = jobId.ToString("D") }, cancellationToken).ConfigureAwait(false);
                await CommitAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID must not be empty.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Worker ID must not be blank.", nameof(workerId));
        if (string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("Failure reason must not be blank.", nameof(error));
        cancellationToken.ThrowIfCancellationRequested();

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                var job = await ReadByIdAsync(connection, jobId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Job '{jobId}' was not found.");
                if (job.State != JobState.Leased || !string.Equals(job.LeaseOwner, workerId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Only the lease owner can fail a leased job.");

                var attempts = job.Attempts + 1;
                var storedError = error.Length > MaxErrorChars ? error[..MaxErrorChars] : error;
                if (attempts >= job.MaxAttempts)
                {
                    await ExecuteAsync(connection,
                        "UPDATE jobs SET State = 3, Attempts = @attempts, LeaseOwner = NULL, LeaseExpiresUtc = NULL, LastError = @error WHERE Id = @id;",
                        new Dictionary<string, object?> { ["@id"] = jobId.ToString("D"), ["@attempts"] = attempts, ["@error"] = storedError },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var delay = QueueRetryPolicy.ComputeDelay(jobId, attempts, _options.BaseRetryDelay, _options.MaxRetryDelay, _options.JitterFactor);
                    var visible = _clock.UtcNow + delay;
                    await ExecuteAsync(connection,
                        "UPDATE jobs SET State = 0, Attempts = @attempts, LeaseOwner = NULL, LeaseExpiresUtc = NULL, NextVisibleUtc = @visible, LastError = @error WHERE Id = @id;",
                        new Dictionary<string, object?>
                        {
                            ["@id"] = jobId.ToString("D"),
                            ["@attempts"] = attempts,
                            ["@visible"] = Format(visible),
                            ["@error"] = storedError,
                        }, cancellationToken).ConfigureAwait(false);
                }

                await CommitAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, COUNT(*) FROM jobs GROUP BY State;";
        var counts = new int[4];
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                counts[reader.GetInt32(0)] = reader.GetInt32(1);
        }

        return new QueueStats(counts[0], counts[1], counts[2], counts[3]);
    }

    public void Dispose()
    {
        _disposed = true;
        _writer.Dispose();
    }

    private async Task<QueueJob> EnqueueOneAsync(SqliteConnection connection, QueueEnqueueItem item, CancellationToken cancellationToken)
    {
        var existing = await ReadActiveAsync(connection, item.FileId, item.JobType, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var options = item.Options ?? new EnqueueOptions();
        var now = _clock.UtcNow;
        var job = new QueueJob(Guid.NewGuid(), item.FileId, item.JobType, JobState.Queued,
            options.Priority, 0, options.MaxAttempts, now, now, null, null,
            item.PayloadJson, options.NormalizedDriveId, null);
        try
        {
            await ExecuteAsync(connection,
                """
                INSERT INTO jobs (Id, FileId, JobType, State, Priority, Attempts, MaxAttempts, CreatedUtc, NextVisibleUtc, LeaseOwner, LeaseExpiresUtc, PayloadJson, DriveId, LastError)
                VALUES (@id, @file, @type, 0, @priority, 0, @maxAttempts, @created, @visible, NULL, NULL, @payload, @drive, NULL);
                """,
                new Dictionary<string, object?>
                {
                    ["@id"] = job.Id.ToString("D"),
                    ["@file"] = job.FileId.ToString("D"),
                    ["@type"] = (int)job.JobType,
                    ["@priority"] = job.Priority,
                    ["@maxAttempts"] = job.MaxAttempts,
                    ["@created"] = Format(job.CreatedUtc),
                    ["@visible"] = Format(job.NextVisibleUtc),
                    ["@payload"] = job.PayloadJson,
                    ["@drive"] = (object?)job.DriveId ?? DBNull.Value,
                }, cancellationToken).ConfigureAwait(false);
            return job;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            var raced = await ReadActiveAsync(connection, item.FileId, item.JobType, cancellationToken).ConfigureAwait(false);
            if (raced is not null)
                return raced;
            throw;
        }
    }

    private static async Task<QueueJob?> ReadActiveAsync(SqliteConnection connection, Guid fileId, JobType jobType, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FileId, JobType, State, Priority, Attempts, MaxAttempts, CreatedUtc, NextVisibleUtc, LeaseOwner, LeaseExpiresUtc, PayloadJson, DriveId, LastError FROM jobs WHERE FileId = @file AND JobType = @type AND State IN (0, 1) LIMIT 1;";
        command.Parameters.AddWithValue("@file", fileId.ToString("D"));
        command.Parameters.AddWithValue("@type", (int)jobType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    private static async Task<QueueJob?> ReadByIdAsync(SqliteConnection connection, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FileId, JobType, State, Priority, Attempts, MaxAttempts, CreatedUtc, NextVisibleUtc, LeaseOwner, LeaseExpiresUtc, PayloadJson, DriveId, LastError FROM jobs WHERE Id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    private static async Task<List<QueueJob>> ReadCandidatesAsync(SqliteConnection connection, QueueClaimRequest request, string nowText, CancellationToken cancellationToken)
    {
        var sql = new System.Text.StringBuilder(
            "SELECT Id, FileId, JobType, State, Priority, Attempts, MaxAttempts, CreatedUtc, NextVisibleUtc, LeaseOwner, LeaseExpiresUtc, PayloadJson, DriveId, LastError FROM jobs WHERE State = 0 AND NextVisibleUtc <= @now");
        var parameters = new Dictionary<string, object?> { ["@now"] = nowText };
        if (request.AllowedJobTypes is { Count: > 0 })
        {
            var names = new List<string>();
            for (var index = 0; index < request.AllowedJobTypes.Count; index++)
            {
                var name = "@type" + index;
                names.Add(name);
                parameters[name] = (int)request.AllowedJobTypes[index];
            }

            sql.Append(" AND JobType IN (").Append(string.Join(", ", names)).Append(')');
        }

        if (request.DriveScope is { Count: > 0 })
        {
            var names = new List<string>();
            for (var index = 0; index < request.DriveScope.Count; index++)
            {
                var name = "@drive" + index;
                names.Add(name);
                parameters[name] = request.DriveScope[index];
            }

            sql.Append(" AND ((DriveId IS NULL) OR (DriveId IN (").Append(string.Join(", ", names)).Append(")))");
        }

        sql.Append(" ORDER BY Priority DESC, CreatedUtc ASC LIMIT ").Append(MaxClaimScan).Append(';');

        var results = new List<QueueJob>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        foreach (var pair in parameters)
            command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadJob(reader));
        return results;
    }

    private static async Task<Dictionary<string, int>> ReadLeasedCountsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(DriveId, ''), COUNT(*) FROM jobs WHERE State = 1 GROUP BY COALESCE(DriveId, '');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private static QueueJob ReadJob(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        (JobType)reader.GetInt32(2),
        (JobState)reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));

    private static bool IsPayloadUsable(string payloadJson)
    {
        try
        {
            QueuePayloadValidator.Validate(payloadJson);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string alterSql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info(@table) WHERE name = @column;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        var existing = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null || existing == DBNull.Value)
            await ExecuteAsync(connection, alterSql, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, Dictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var pair in parameters)
                command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BeginImmediateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CommitAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "COMMIT;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollbackAsync(SqliteConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ROLLBACK;";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = " + _options.BusyTimeoutMs + ";";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("o");
}
