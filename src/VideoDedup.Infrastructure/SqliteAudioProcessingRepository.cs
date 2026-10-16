using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

public sealed class SqliteAudioProcessingRepository(string databasePath) : IAudioProcessingRepository
{
    private readonly string _path = Path.GetFullPath(databasePath);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (!_initialized)
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!_initialized)
                    {
                        using var transaction = connection.BeginTransaction(deferred: false);
                        using var schema = connection.CreateCommand(); schema.Transaction = transaction;
                        schema.CommandText = """
                            CREATE TABLE IF NOT EXISTS audio_results (
                                FileId TEXT PRIMARY KEY NOT NULL,
                                SizeBytes INTEGER NOT NULL,
                                ModifiedUtc TEXT NOT NULL,
                                Identity TEXT NULL,
                                Algorithm TEXT NOT NULL,
                                Major INTEGER NOT NULL,
                                Minor INTEGER NOT NULL,
                                DurationTicks INTEGER NOT NULL,
                                HasAudio INTEGER NOT NULL CHECK(HasAudio IN (0,1)),
                                Fingerprint BLOB NOT NULL CHECK(length(Fingerprint)<=262144),
                                Failure INTEGER NULL
                            );
                            """;
                        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        transaction.Commit();
                        _initialized = true;
                    }
                }
                finally { _gate.Release(); }
            }
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task SaveAsync(AudioProcessingRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var bytes = record.Fingerprint;
        if (record.FileId == Guid.Empty || record.Revision is null || record.Revision.SizeBytes < 0 ||
            record.Algorithm is null || record.Algorithm.Length is < 1 or > 64 || record.Version.Major <= 0 || record.Version.Minor < 0 ||
            record.Duration < TimeSpan.Zero || record.Duration > FrameSamplingPolicy.DurationLimit || bytes.Length > FpcalcAudioFingerprinter.MaxFingerprintBytes ||
            (record.HasAudio && (bytes.Length == 0 || record.Duration <= TimeSpan.Zero || record.Failure is not null)) ||
            (!record.HasAudio && (bytes.Length != 0 || record.Duration != TimeSpan.Zero)) ||
            (record.Failure is not null && !Enum.IsDefined(record.Failure.Value)))
            throw new ArgumentException("Invalid audio record.", nameof(record));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audio_results VALUES (@id,@size,@modified,@identity,@algorithm,@major,@minor,@duration,@hasAudio,@fingerprint,@failure)
            ON CONFLICT(FileId) DO UPDATE SET SizeBytes=excluded.SizeBytes, ModifiedUtc=excluded.ModifiedUtc, Identity=excluded.Identity,
                Algorithm=excluded.Algorithm, Major=excluded.Major, Minor=excluded.Minor, DurationTicks=excluded.DurationTicks,
                HasAudio=excluded.HasAudio, Fingerprint=excluded.Fingerprint, Failure=excluded.Failure;
            """;
        command.Parameters.AddWithValue("@id", record.FileId.ToString("D"));
        command.Parameters.AddWithValue("@size", record.Revision.SizeBytes);
        command.Parameters.AddWithValue("@modified", record.Revision.ModifiedUtc.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("@identity", (object?)record.Revision.FileIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("@algorithm", record.Algorithm);
        command.Parameters.AddWithValue("@major", record.Version.Major);
        command.Parameters.AddWithValue("@minor", record.Version.Minor);
        command.Parameters.AddWithValue("@duration", record.Duration.Ticks);
        command.Parameters.AddWithValue("@hasAudio", record.HasAudio ? 1 : 0);
        command.Parameters.AddWithValue("@fingerprint", bytes);
        command.Parameters.AddWithValue("@failure", record.Failure is null ? DBNull.Value : (int)record.Failure.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public async Task<AudioProcessingRecord?> GetAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SizeBytes,ModifiedUtc,Identity,Algorithm,Major,Minor,DurationTicks,HasAudio,Fingerprint,Failure FROM audio_results WHERE FileId=@id;";
        command.Parameters.AddWithValue("@id", fileId.ToString("D"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            var revision = new FileRevision(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.IsDBNull(2) ? null : reader.GetString(2));
            var algorithm = reader.GetString(3);
            var version = FingerprintVersion.Create(reader.GetInt32(4), reader.GetInt32(5));
            var duration = TimeSpan.FromTicks(reader.GetInt64(6));
            var hasAudio = reader.GetInt32(7) switch { 0 => false, 1 => true, _ => throw new InvalidDataException() };
            var fingerprint = (byte[])reader[8];
            MediaFailure? failure = reader.IsDBNull(9) ? null : (MediaFailure)reader.GetInt32(9);
            var record = AudioProcessingRecord.Create(fileId, revision, algorithm, version, duration, fingerprint, hasAudio, failure);
            if (revision.SizeBytes < 0 || algorithm.Length is < 1 or > 64 || version.Major <= 0 || version.Minor < 0 ||
                duration < TimeSpan.Zero || duration > FrameSamplingPolicy.DurationLimit || fingerprint.Length > FpcalcAudioFingerprinter.MaxFingerprintBytes ||
                (hasAudio && (fingerprint.Length == 0 || duration <= TimeSpan.Zero || failure is not null)) ||
                (!hasAudio && (fingerprint.Length != 0 || duration != TimeSpan.Zero)) ||
                (failure is not null && !Enum.IsDefined(failure.Value)))
                throw new InvalidDataException();
            return record;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or InvalidDataException)
        { throw new MediaProcessingException(MediaFailure.InvalidData); }
    }
}
