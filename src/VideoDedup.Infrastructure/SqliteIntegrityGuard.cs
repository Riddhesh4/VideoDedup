using Microsoft.Data.Sqlite;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

public enum DatabaseHealth
{
    /// <summary>The database opened and reported itself consistent.</summary>
    Healthy,

    /// <summary>No database exists yet; the next initialize creates one.</summary>
    Missing,

    /// <summary>The database was corrupt; it was moved aside and the next initialize creates a fresh one.</summary>
    Recovered,
}

/// <summary>Outcome of starting a session. A recovered database always names the file that kept the broken data.</summary>
public sealed record DatabaseIntegrityReport(DatabaseHealth Health, string DatabasePath, string? BackupPath, string Detail);

/// <summary>
/// Crash and corruption recovery for the index database. A corrupt file is never deleted and never silently
/// repaired in place: it is moved aside intact so the user (or support) can inspect it, and the application starts
/// with a fresh, empty index instead of failing to launch.
/// </summary>
public sealed class SqliteIntegrityGuard(IQueueClock? clock = null)
{
    private readonly IQueueClock _clock = clock ?? SystemQueueClock.Instance;

    public async Task<DatabaseIntegrityReport> VerifyAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("The database path must be absolute.", nameof(databasePath));
        }

        var full = Path.GetFullPath(databasePath);
        if (!File.Exists(full))
        {
            return new DatabaseIntegrityReport(DatabaseHealth.Missing, full, null, "No index database yet.");
        }

        string? problem = null;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                problem = $"integrity_check reported '{PrivacyText.Sanitize(result)}'";
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException)
        {
            problem = $"the file could not be read ({exception.GetType().Name})";
        }

        if (problem is null)
        {
            return new DatabaseIntegrityReport(DatabaseHealth.Healthy, full, null, "Integrity check passed.");
        }

        var backup = MoveAside(full);
        return new DatabaseIntegrityReport(DatabaseHealth.Recovered, full, backup,
            $"The index database was unusable ({problem}); it was kept as '{Path.GetFileName(backup)}' and a new index will be created.");
    }

    private string MoveAside(string databasePath)
    {
        var suffix = $".corrupt-{_clock.UtcNow:yyyyMMdd'T'HHmmss'Z'}";
        var backup = databasePath + suffix;
        var attempt = 1;
        while (File.Exists(backup))
        {
            backup = $"{databasePath}{suffix}-{attempt++}";
        }

        File.Move(databasePath, backup);
        foreach (var companion in new[] { "-wal", "-shm" })
        {
            var source = databasePath + companion;
            if (!File.Exists(source))
            {
                continue;
            }

            try
            {
                File.Move(source, backup + companion);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover write-ahead file is harmless next to a moved-aside database.
            }
        }

        return backup;
    }
}
