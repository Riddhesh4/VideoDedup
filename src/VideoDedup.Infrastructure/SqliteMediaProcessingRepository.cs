using System.Text.Json;
using Microsoft.Data.Sqlite;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

public sealed class SqliteMediaProcessingRepository(string databasePath) : IMediaProcessingRepository
{
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath) }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=ON;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS media_results (
                    FileId TEXT PRIMARY KEY NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    ModifiedUtc TEXT NOT NULL,
                    MetadataJson TEXT NULL,
                    Sha256 TEXT NULL,
                    Failure INTEGER NULL,
                    Identity TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('media_results') WHERE name='Identity';";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
            {
                using var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE media_results ADD COLUMN Identity TEXT NULL;";
                await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task SaveAsync(MediaProcessingRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_results (FileId,SizeBytes,ModifiedUtc,MetadataJson,Sha256,Failure,Identity) VALUES (@id,@size,@modified,@metadata,@hash,@failure,@identity)
            ON CONFLICT(FileId) DO UPDATE SET
                SizeBytes=excluded.SizeBytes, ModifiedUtc=excluded.ModifiedUtc, Identity=excluded.Identity,
                MetadataJson=CASE WHEN excluded.Failure IS NULL AND media_results.SizeBytes=excluded.SizeBytes AND media_results.ModifiedUtc=excluded.ModifiedUtc AND media_results.Identity IS excluded.Identity
                    THEN COALESCE(excluded.MetadataJson,media_results.MetadataJson) ELSE excluded.MetadataJson END,
                Sha256=CASE WHEN excluded.Failure IS NULL AND media_results.SizeBytes=excluded.SizeBytes AND media_results.ModifiedUtc=excluded.ModifiedUtc AND media_results.Identity IS excluded.Identity
                    THEN COALESCE(excluded.Sha256,media_results.Sha256) ELSE excluded.Sha256 END,
                Failure=CASE WHEN excluded.Failure IS NULL AND excluded.MetadataJson IS NULL
                    AND media_results.SizeBytes=excluded.SizeBytes AND media_results.ModifiedUtc=excluded.ModifiedUtc
                    AND media_results.Identity IS excluded.Identity THEN media_results.Failure ELSE excluded.Failure END;
            """;
        command.Parameters.AddWithValue("@identity", (object?)record.Revision.FileIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", record.FileId.ToString("D"));
        command.Parameters.AddWithValue("@size", record.Revision.SizeBytes);
        command.Parameters.AddWithValue("@modified", record.Revision.ModifiedUtc.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("@metadata", record.Metadata is null ? DBNull.Value : JsonSerializer.Serialize(record.Metadata));
        command.Parameters.AddWithValue("@hash", (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("@failure", record.Failure is null ? DBNull.Value : (int)record.Failure);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaProcessingRecord?> GetAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SizeBytes,ModifiedUtc,MetadataJson,Sha256,Failure,Identity FROM media_results WHERE FileId=@id;";
        command.Parameters.AddWithValue("@id", fileId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new MediaProcessingRecord(fileId, new FileRevision(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.IsDBNull(5) ? null : reader.GetString(5)),
            reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<MediaMetadata>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : (MediaFailure)reader.GetInt32(4));
    }
}
