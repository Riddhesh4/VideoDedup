using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

public sealed class SqliteVisualProcessingRepository(string databasePath) : IVisualProcessingRepository
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SchemaGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _schemaGate = SchemaGates.GetOrAdd(Path.GetFullPath(databasePath), static _ => new SemaphoreSlim(1, 1));
    private bool _initialized;
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath) }.ToString());
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var setup = connection.CreateCommand();
            setup.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (!_initialized)
            {
                await _schemaGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var schemaNeeded = !_initialized;
                    if (schemaNeeded)
                    {
                        using var transaction = connection.BeginTransaction(deferred: false);
                        using var schema = connection.CreateCommand(); schema.Transaction = transaction;
                        schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS visual_runs (
                        FileId TEXT PRIMARY KEY NOT NULL, SizeBytes INTEGER NOT NULL, ModifiedUtc TEXT NOT NULL, Identity TEXT NULL,
                        Algorithm TEXT NOT NULL, Major INTEGER NOT NULL, Minor INTEGER NOT NULL,
                        IntervalTicks INTEGER NOT NULL, MaxFrames INTEGER NOT NULL, Failure INTEGER NULL
                    );
                    CREATE TABLE IF NOT EXISTS visual_frames (
                        FileId TEXT NOT NULL REFERENCES visual_runs(FileId) ON DELETE CASCADE,
                        Ordinal INTEGER NOT NULL CHECK(Ordinal>=0 AND Ordinal<120),
                        TimestampTicks INTEGER NOT NULL CHECK(TimestampTicks>=0), Hash TEXT NOT NULL,
                        Quality TEXT NOT NULL, Disposition INTEGER NOT NULL,
                        PRIMARY KEY(FileId,Ordinal)
                    );
                    """;
                        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        transaction.Commit(); _initialized = true;
                    }
                }
                finally { _schemaGate.Release(); }
            }
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task SaveAsync(VisualProcessingRecord record, CancellationToken cancellationToken)
    {
        if (record is null || record.Revision is null || record.Algorithm is null || record.Frames is null ||
            record.FileId == Guid.Empty || record.Revision.SizeBytes < 0 || record.Algorithm.Length is < 1 or > 64 ||
            record.Version.Major <= 0 || record.Version.Minor < 0 || record.MaxFrames is < 1 or > FrameSamplingPolicy.FrameLimit ||
            record.Frames.Count > record.MaxFrames || record.IntervalTicks < TimeSpan.FromSeconds(0.25).Ticks ||
            record.IntervalTicks > TimeSpan.FromHours(1).Ticks || (record.Failure is null && record.Frames.Count == 0) ||
            (record.Failure is not null && (record.Frames.Count != 0 || !Enum.IsDefined(record.Failure.Value))) ||
            record.Frames.Any(f => f is null || f.Version != record.Version || f.Timestamp > FrameSamplingPolicy.DurationLimit || !Enum.IsDefined(f.Disposition)) ||
            record.Frames.Zip(record.Frames.Skip(1)).Any(pair => pair.First.Timestamp >= pair.Second.Timestamp))
            throw new ArgumentException("Invalid visual record.", nameof(record));
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM visual_runs WHERE FileId=@id;
                INSERT INTO visual_runs VALUES (@id,@size,@modified,@identity,@algorithm,@major,@minor,@interval,@max,@failure);
                """;
            command.Parameters.AddWithValue("@id", record.FileId.ToString("D"));
            command.Parameters.AddWithValue("@size", record.Revision.SizeBytes);
            command.Parameters.AddWithValue("@modified", record.Revision.ModifiedUtc.ToUniversalTime().ToString("o"));
            command.Parameters.AddWithValue("@identity", (object?)record.Revision.FileIdentity ?? DBNull.Value);
            command.Parameters.AddWithValue("@algorithm", record.Algorithm);
            command.Parameters.AddWithValue("@major", record.Version.Major); command.Parameters.AddWithValue("@minor", record.Version.Minor);
            command.Parameters.AddWithValue("@interval", record.IntervalTicks); command.Parameters.AddWithValue("@max", record.MaxFrames);
            command.Parameters.AddWithValue("@failure", record.Failure is null ? DBNull.Value : (int)record.Failure);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < record.Frames.Count; index++)
            {
                var frame = record.Frames[index];
                using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO visual_frames VALUES (@id,@ordinal,@time,@hash,@quality,@disposition);";
                insert.Parameters.AddWithValue("@id", record.FileId.ToString("D")); insert.Parameters.AddWithValue("@ordinal", index);
                insert.Parameters.AddWithValue("@time", frame.Timestamp.Ticks);
                insert.Parameters.AddWithValue("@hash", frame.PerceptualHash.ToString("X16", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("@quality", frame.Quality.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("@disposition", (int)frame.Disposition);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
        }
        finally { _writer.Release(); }
    }
    public async Task<VisualProcessingRecord?> GetAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT * FROM visual_runs WHERE FileId=@id;";
            command.Parameters.AddWithValue("@id", fileId.ToString("D"));
            VisualProcessingRecord result;
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
                result = new VisualProcessingRecord(fileId,
                    new FileRevision(reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.IsDBNull(3) ? null : reader.GetString(3)),
                    reader.GetString(4), FingerprintVersion.Create(reader.GetInt32(5), reader.GetInt32(6)), reader.GetInt64(7), reader.GetInt32(8), [],
                    reader.IsDBNull(9) ? null : (MediaFailure)reader.GetInt32(9));
            }
            using var framesCommand = connection.CreateCommand(); framesCommand.Transaction = transaction;
            framesCommand.CommandText = "SELECT TimestampTicks,Hash,Quality,Disposition FROM visual_frames WHERE FileId=@id ORDER BY Ordinal LIMIT 121;";
            framesCommand.Parameters.AddWithValue("@id", fileId.ToString("D"));
            using var framesReader = await framesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var frames = new List<VisualFingerprint>();
            while (await framesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                frames.Add(VisualFingerprint.Create(TimeSpan.FromTicks(framesReader.GetInt64(0)),
                    ulong.Parse(framesReader.GetString(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    decimal.Parse(framesReader.GetString(2), CultureInfo.InvariantCulture), result.Version, (FrameDisposition)framesReader.GetInt32(3)));
            if (result.Version.Major <= 0 || result.Version.Minor < 0 ||
                frames.Count > FrameSamplingPolicy.FrameLimit || result.MaxFrames is < 1 or > FrameSamplingPolicy.FrameLimit ||
                result.IntervalTicks < TimeSpan.FromSeconds(0.25).Ticks || result.IntervalTicks > TimeSpan.FromHours(1).Ticks ||
                result.Algorithm.Length is < 1 or > 64 || frames.Count > result.MaxFrames ||
                frames.Zip(frames.Skip(1)).Any(pair => pair.First.Timestamp >= pair.Second.Timestamp) ||
                (result.Failure is null ? frames.Count == 0 : frames.Count != 0 || !Enum.IsDefined(result.Failure.Value)))
                throw new MediaProcessingException(MediaFailure.InvalidData);
            return result with { Frames = frames.AsReadOnly() };
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        { throw new MediaProcessingException(MediaFailure.InvalidData); }
        finally { _writer.Release(); }
    }
}
