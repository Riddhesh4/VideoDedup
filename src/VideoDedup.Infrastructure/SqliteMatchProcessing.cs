using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VideoDedup.Application;
using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Infrastructure;

/// <summary>Canonical, upsert-based persistence for decided match pairs in the shared SQLite database.</summary>
public sealed class SqliteMatchResultRepository(string databasePath) : IMatchResultRepository
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
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync(token).ConfigureAwait(false);
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
                            CREATE TABLE IF NOT EXISTS match_results (
                                FileIdA TEXT NOT NULL,
                                FileIdB TEXT NOT NULL,
                                MatchType INTEGER NOT NULL,
                                Score TEXT NOT NULL,
                                AudioOffsetSeconds TEXT NULL,
                                PolicyMajor INTEGER NOT NULL,
                                PolicyMinor INTEGER NOT NULL,
                                VisualMajor INTEGER NULL,
                                VisualMinor INTEGER NULL,
                                AudioMajor INTEGER NULL,
                                AudioMinor INTEGER NULL,
                                Reasons TEXT NOT NULL,
                                RecordedUtc TEXT NOT NULL,
                                PRIMARY KEY(FileIdA, FileIdB)
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

    public async Task SaveAsync(MatchPersistenceRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var reasons = JsonSerializer.Serialize(record.Reasons);
        var (fileIdA, fileIdB) = (record.FileIdA.ToString("D"), record.FileIdB.ToString("D"));
        if (string.CompareOrdinal(fileIdA, fileIdB) >= 0)
            throw new ArgumentException("Match pair must be persisted in canonical order.");

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO match_results (FileIdA, FileIdB, MatchType, Score, AudioOffsetSeconds, PolicyMajor, PolicyMinor,
                VisualMajor, VisualMinor, AudioMajor, AudioMinor, Reasons, RecordedUtc)
            VALUES (@a,@b,@type,@score,@offset,@pm,@pn,@vm,@vn,@am,@an,@reasons,@recorded)
            ON CONFLICT(FileIdA, FileIdB) DO UPDATE SET MatchType=excluded.MatchType, Score=excluded.Score,
                AudioOffsetSeconds=excluded.AudioOffsetSeconds, PolicyMajor=excluded.PolicyMajor, PolicyMinor=excluded.PolicyMinor,
                VisualMajor=excluded.VisualMajor, VisualMinor=excluded.VisualMinor, AudioMajor=excluded.AudioMajor,
                AudioMinor=excluded.AudioMinor, Reasons=excluded.Reasons, RecordedUtc=excluded.RecordedUtc;
            """;
        command.Parameters.AddWithValue("@a", fileIdA);
        command.Parameters.AddWithValue("@b", fileIdB);
        command.Parameters.AddWithValue("@type", (int)record.MatchType);
        command.Parameters.AddWithValue("@score", FormatDecimal(record.OverallScore));
        command.Parameters.AddWithValue("@offset", record.AudioOffsetSeconds is null ? DBNull.Value : FormatDecimal(record.AudioOffsetSeconds.Value));
        command.Parameters.AddWithValue("@pm", record.PolicyVersion.Major);
        command.Parameters.AddWithValue("@pn", record.PolicyVersion.Minor);
        command.Parameters.AddWithValue("@vm", record.EvidenceVersions.VisualVersion?.Major ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@vn", record.EvidenceVersions.VisualVersion?.Minor ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@am", record.EvidenceVersions.AudioVersion?.Major ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@an", record.EvidenceVersions.AudioVersion?.Minor ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@reasons", reasons);
        command.Parameters.AddWithValue("@recorded", DateTimeOffset.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var problems = Valid(record);
        if (problems.Count > 0) // Post-write revalidation: corrupt in-memory records never commit silently.
        {
            transaction.Rollback();
            throw new ArgumentException($"Match record rejected by invariant revalidation ({string.Join(',', problems)}).", nameof(record));
        }
        transaction.Commit();
    }

    /// <summary>Reads every decided pair defensively; corrupt rows surface typed InvalidData instead of being served.</summary>
    public async Task<IReadOnlyList<MatchPersistenceRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT FileIdA,FileIdB,MatchType,Score,AudioOffsetSeconds,PolicyMajor,PolicyMinor,VisualMajor,VisualMinor,AudioMajor,AudioMinor,Reasons FROM match_results ORDER BY FileIdA,FileIdB;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<MatchPersistenceRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var fileIdA = Guid.Parse(reader.GetString(0));
                var fileIdB = Guid.Parse(reader.GetString(1));
                if (string.CompareOrdinal(fileIdA.ToString("D"), fileIdB.ToString("D")) >= 0)
                    throw new InvalidDataException();
                var matchType = (MatchType)reader.GetInt32(2);
                var score = decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
                decimal? offset = reader.IsDBNull(4) ? null : decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
                var policyVersion = FingerprintVersion.Create(reader.GetInt32(5), reader.GetInt32(6));
                FingerprintVersion? visualVersion = reader.IsDBNull(7) ? null : FingerprintVersion.Create(reader.GetInt32(7), reader.GetInt32(8));
                FingerprintVersion? audioVersion = reader.IsDBNull(9) ? null : FingerprintVersion.Create(reader.GetInt32(9), reader.GetInt32(10));
                var reasons = JsonSerializer.Deserialize<string[]>(reader.GetString(11));
                if (reasons is null || reasons.Length == 0) throw new InvalidDataException();
                rows.Add(MatchPersistenceRecord.Create(fileIdA, fileIdB, matchType, score,
                    new EvidenceVersions(visualVersion, audioVersion), policyVersion, reasons, offset));
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException
                or ArgumentException or JsonException)
            {
                throw new MediaProcessingException(MediaFailure.InvalidData);
            }
        }
        return rows;
    }

    /// <summary>Deletes a pair row (used to retract decisions when evidence changes).</summary>
    public async Task<bool> DeleteAsync(Guid fileIdA, Guid fileIdB, CancellationToken cancellationToken)
    {
        var (first, second) = MatchPairExtensions.CanonicalPair(fileIdA, fileIdB);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM match_results WHERE FileIdA=@a AND FileIdB=@b;";
        command.Parameters.AddWithValue("@a", first.ToString("D"));
        command.Parameters.AddWithValue("@b", second.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static IReadOnlyCollection<string> Valid(MatchPersistenceRecord record)
    {
        var problems = new List<string>();
        if (record.FileIdA == Guid.Empty || record.FileIdB == Guid.Empty) problems.Add("pair");
        if (record.OverallScore is < 0m or > 1m) problems.Add("score");
        if (record.Reasons.Count == 0) problems.Add("reasons");
        return problems;
    }
}

/// <summary>Assembles fully-processed fingerprint evidence for every indexed file across all enabled roots.</summary>
public sealed class MatchEvidenceSource(IDiscoveryRepository discovery, IScanRootRepository roots,
    IMediaProcessingRepository media, IVisualProcessingRepository visuals,
    IAudioProcessingRepository? audios, FrameSamplingPolicy? sampling = null) : IMatchEvidenceSource
{
    private readonly FrameSamplingPolicy _sampling = sampling ?? new();

    public async Task<IReadOnlyList<FileMatchEvidence>> GetEvidenceAsync(CancellationToken cancellationToken)
    {
        var evidence = new List<FileMatchEvidence>();
        foreach (var root in await roots.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var files = await discovery.GetFilesAsync(root.Id, cancellationToken).ConfigureAwait(false);
            foreach (var file in files)
            {
                var result = await media.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                if (result?.Sha256 is null || result.Metadata is null) continue;
                var visual = await visuals.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                var audio = audios is null ? null : await audios.GetAsync(file.Id, cancellationToken).ConfigureAwait(false);
                // A file participates in matching only when both perceptual stores are current and valid;
                // failed or stale processing is retried by the fingerprint workers, not matched here.
                var ready = visual is { Failure: null } && visual.IsCurrent(result.Revision, _sampling) &&
                    audio is { Failure: null } && audio.IsCurrent(result.Revision);
                if (!ready || visual is null) continue;
                evidence.Add(new FileMatchEvidence(file.Id, root.Id, result.Sha256, visual.Frames,
                    audio is { HasAudio: true } ? AudioSnapshot(audio) : null, IsReady: true));
            }
        }

        return evidence;
    }

    private static AudioFingerprint AudioSnapshot(AudioProcessingRecord record) =>
        AudioFingerprint.Create(TimeSpan.Zero, record.Duration, record.Fingerprint, record.Version);
}
