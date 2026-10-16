using VideoDedup.Application;

namespace VideoDedup.Application.Tests;

/// <summary>Catalog fake; counts reads so tests can prove the service avoids unnecessary I/O.</summary>
internal sealed class FakeCatalog(params IndexedFileEntry[] entries) : IIndexedFileCatalog
{
    public int Reads { get; private set; }

    public Task<IReadOnlyList<IndexedFileEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        Reads++;
        IReadOnlyList<IndexedFileEntry> snapshot = entries;
        return Task.FromResult(snapshot);
    }
}

internal sealed class FakeDecisionStore : IReviewDecisionStore
{
    public Dictionary<(string GroupKey, Guid FileId), ReviewDecisionRecord> Records { get; } = new();

    public List<ReviewDecisionRecord> Saves { get; } = new();

    public Task SaveAsync(ReviewDecisionRecord record, CancellationToken cancellationToken)
    {
        Saves.Add(record);
        Records[(record.GroupKey, record.FileId)] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReviewDecisionRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ReviewDecisionRecord> snapshot = Records.Values.ToArray();
        return Task.FromResult(snapshot);
    }
}

internal sealed class FakeMatchRepository : IMatchResultRepository
{
    public List<MatchPersistenceRecord> Rows { get; } = new();

    public Task SaveAsync(MatchPersistenceRecord record, CancellationToken cancellationToken)
    {
        Rows.Add(record);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid fileIdA, Guid fileIdB, CancellationToken cancellationToken)
    {
        var pair = MatchPairExtensions.CanonicalPair(fileIdA, fileIdB);
        return Task.FromResult(Rows.RemoveAll(row => row.FileIdA == pair.FileIdA && row.FileIdB == pair.FileIdB) > 0);
    }

    public Task<IReadOnlyList<MatchPersistenceRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MatchPersistenceRecord> snapshot = Rows.ToArray();
        return Task.FromResult(snapshot);
    }
}

/// <summary>In-memory quarantine log with the same transitions as the SQLite store.</summary>
internal sealed class FakeQuarantineLog(IQueueClock? clock = null) : IQuarantineLog
{
    private readonly IQueueClock _clock = clock ?? new SystemQueueClock();

    public List<QuarantineRecord> Records { get; } = new();

    public Task<QuarantineRecord> BeginAsync(Guid fileId, string sourcePath, string destinationPath, bool crossVolume,
        CancellationToken cancellationToken)
    {
        var record = QuarantineRecord.Create(Guid.NewGuid(), fileId, sourcePath, destinationPath,
            QuarantineState.Pending, crossVolume, _clock.UtcNow);
        Records.Add(record);
        return Task.FromResult(record);
    }

    public Task MarkQuarantinedAsync(Guid recordId, CancellationToken cancellationToken) =>
        Transition(recordId, QuarantineState.Pending, QuarantineState.Quarantined, null);

    public Task MarkRestoredAsync(Guid recordId, CancellationToken cancellationToken) =>
        Transition(recordId, QuarantineState.Quarantined, QuarantineState.Restored, null);

    public Task MarkFailedAsync(Guid recordId, string reason, CancellationToken cancellationToken) =>
        Transition(recordId, QuarantineState.Pending, QuarantineState.Failed, reason);

    public Task<IReadOnlyList<QuarantineRecord>> GetAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QuarantineRecord> snapshot = Records.ToArray();
        return Task.FromResult(snapshot);
    }

    /// <summary>Replaces a stored row so a recovery test can simulate an interrupted process.</summary>
    public void Overwrite(QuarantineRecord record)
    {
        var index = Records.FindIndex(existing => existing.RecordId == record.RecordId);
        Records[index] = record;
    }

    public void AddRaw(QuarantineRecord record) => Records.Add(record);

    private Task Transition(Guid recordId, QuarantineState expected, QuarantineState target, string? reason)
    {
        var index = Records.FindIndex(record => record.RecordId == recordId);
        if (index < 0 || Records[index].State != expected)
        {
            throw new InvalidOperationException($"Quarantine entry '{recordId:D}' is not in the expected {expected} state.");
        }

        Records[index] = Records[index].WithState(target, _clock.UtcNow, reason);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory filesystem that rejects overwrites exactly like the real adapter.</summary>
internal sealed class FakeStorage : IQuarantineStorage
{
    /// <summary>Path prefixes mapped to a volume root, so cross-volume moves are testable without a second drive.</summary>
    public Dictionary<string, string> VolumeRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> UnwritableDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Source, string Destination)> Moves { get; } = new();

    public string? DeniedPath { get; set; }

    public string? FailingPath { get; set; }

    public void SeedFile(string path, long size = 1_000_000)
    {
        Files[path] = size;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            SeedDirectory(directory);
        }
    }

    public void SeedDirectory(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current) && Directories.Add(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    public void RemoveFile(string path) => Files.Remove(path);

    public void RemoveDirectory(string path) => Directories.Remove(Path.GetFullPath(path));

    public StorageInspection Inspect(string path)
    {
        if (FailingPath is not null && string.Equals(FailingPath, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException(StorageFailure.AccessDenied, "Injected inspection failure.");
        }

        if (Files.TryGetValue(path, out var size))
        {
            return new StorageInspection(true, false, false, size, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }

        if (Directories.Contains(Path.GetFullPath(path)))
        {
            return new StorageInspection(true, false, true, 0, null);
        }

        return new StorageInspection(false, false, false, 0, null);
    }

    public bool DirectoryExists(string path) => Directories.Contains(Path.GetFullPath(path));

    public bool IsWritableDirectory(string path) => !UnwritableDirectories.Contains(Path.GetFullPath(path));

    public string? GetVolumeRoot(string path)
    {
        // Mirrors the real adapter: normalize first, so mixed separators cannot defeat prefix matching.
        var normalized = Path.GetFullPath(path);
        foreach (var pair in VolumeRoots.OrderByDescending(pair => pair.Key.Length))
        {
            if (normalized.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return Path.GetPathRoot(normalized);
    }

    public void EnsureDirectory(string path) => SeedDirectory(path);

    public void Move(string source, string destination)
    {
        if (DeniedPath is not null && string.Equals(DeniedPath, source, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException(StorageFailure.AccessDenied, "Injected move denial.");
        }

        if (!Files.TryGetValue(source, out var size))
        {
            throw new StorageException(StorageFailure.NotFound, "The source file does not exist.");
        }

        if (Files.ContainsKey(destination) || Directories.Contains(Path.GetFullPath(destination)))
        {
            throw new StorageException(StorageFailure.Conflict, "The destination already exists; nothing was overwritten.");
        }

        Moves.Add((source, destination));
        Files[destination] = size;
        Files.Remove(source);
    }
}
