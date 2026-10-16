using VideoDedup.Domain;

namespace VideoDedup.Application;

/// <summary>Quarantine service configuration. The fallback directory lives in the per-user app data store.</summary>
public sealed record QuarantineServiceOptions(string FallbackDirectory, int MaxItemsPerBatch = 500)
{
    public QuarantineServiceOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(FallbackDirectory) || !Path.IsPathFullyQualified(FallbackDirectory))
        {
            throw new ArgumentException("The quarantine fallback directory must be an absolute path.", nameof(FallbackDirectory));
        }

        if (MaxItemsPerBatch is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxItemsPerBatch), "Batch size must be between 1 and 5000.");
        }

        return this;
    }
}

/// <summary>
/// Quarantine-first safe action pipeline. Planning never touches a file; executing moves only confirmed items,
/// re-checks the last-copy guard immediately before each move, and records intent in the durable log first so an
/// interrupted batch is always explainable. Undo moves files back and never overwrites anything.
/// </summary>
public sealed class QuarantineService : IQuarantineService
{
    private readonly IIndexedFileCatalog _catalog;
    private readonly IQuarantineStorage _storage;
    private readonly IQuarantineLog _log;
    private readonly QuarantineServiceOptions _options;
    private readonly IQueueClock _clock;

    public QuarantineService(IIndexedFileCatalog catalog, IQuarantineStorage storage, IQuarantineLog log,
        QuarantineServiceOptions options, IQueueClock? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _clock = clock ?? SystemQueueClock.Instance;
    }

    public async Task<QuarantinePlan> PlanAsync(IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken)
    {
        var selection = ValidateSelection(fileIds);
        var entries = await _catalog.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        var index = Index(entries);
        var quarantined = await QuarantinedFileIdsAsync(cancellationToken).ConfigureAwait(false);
        var roots = entries.Select(entry => entry.RootPath).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();

        var items = new List<QuarantinePlanItem>(selection.Count);
        foreach (var fileId in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(Evaluate(fileId, index, roots, quarantined));
        }

        return new QuarantinePlan(items);
    }

    public async Task<QuarantineBatchResult> ExecuteAsync(QuarantinePlan plan, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var outcomes = new List<QuarantineOutcome>();
        var allowed = plan.AllowedItems;
        if (allowed.Count > _options.MaxItemsPerBatch)
        {
            throw new ArgumentException($"A quarantine batch may hold at most {_options.MaxItemsPerBatch} files.", nameof(plan));
        }

        foreach (var blocked in plan.BlockedItems)
        {
            outcomes.Add(QuarantineOutcome.Blocked(blocked.FileId, string.Join(' ', blocked.Reasons)));
        }

        // Nothing moves without explicit confirmation; the caller must decide again with the plan in hand.
        if (allowed.Count > 0 && !confirmed)
        {
            return new QuarantineBatchResult(ConfirmationRequired: true, outcomes);
        }

        var entries = await _catalog.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        var index = Index(entries);
        var quarantined = await QuarantinedFileIdsAsync(cancellationToken).ConfigureAwait(false);
        var roots = entries.Select(entry => entry.RootPath).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();

        foreach (var planned in allowed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Re-evaluated one at a time: a copy moved earlier in this batch is no longer on disk, so the
            // last-copy guard sees the batch's own effects and refuses to empty a group.
            var item = Evaluate(planned.FileId, index, roots, quarantined);
            if (!item.IsAllowed || item.DestinationPath is null)
            {
                outcomes.Add(QuarantineOutcome.Blocked(item.FileId,
                    item.Reasons.Count > 0 ? string.Join(' ', item.Reasons) : "The file can no longer be quarantined safely."));
                continue;
            }

            var destination = ResolveAvailableDestination(item.DestinationPath);
            QuarantineRecord record;
            try
            {
                record = await _log.BeginAsync(item.FileId, item.SourcePath, destination, item.CrossVolume, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                outcomes.Add(QuarantineOutcome.Blocked(item.FileId, "The quarantine log could not be written, so nothing was moved."));
                continue;
            }

            try
            {
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    _storage.EnsureDirectory(directory);
                }

                _storage.Move(item.SourcePath, destination);
                await _log.MarkQuarantinedAsync(record.RecordId, cancellationToken).ConfigureAwait(false);
                outcomes.Add(QuarantineOutcome.Moved(item.FileId, record.RecordId, item.CrossVolume));
            }
            catch (Exception exception) when (exception is StorageException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                var failure = FailureText(exception);
                await _log.MarkFailedAsync(record.RecordId, failure, cancellationToken).ConfigureAwait(false);
                outcomes.Add(QuarantineOutcome.Blocked(item.FileId, failure));
            }
        }

        return new QuarantineBatchResult(ConfirmationRequired: false, outcomes);
    }

    public Task<IReadOnlyList<QuarantineRecord>> GetHistoryAsync(CancellationToken cancellationToken) =>
        _log.GetAsync(cancellationToken);

    public async Task<IReadOnlyList<QuarantineOutcome>> UndoAsync(IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        var selection = recordIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (selection.Length == 0)
        {
            throw new ArgumentException("Select at least one quarantine entry to restore.", nameof(recordIds));
        }

        var history = await _log.GetAsync(cancellationToken).ConfigureAwait(false);
        var byId = history.ToDictionary(record => record.RecordId);
        var outcomes = new List<QuarantineOutcome>(selection.Length);
        foreach (var recordId in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(recordId, out var record))
            {
                outcomes.Add(new QuarantineOutcome(recordId, false, "The quarantine entry no longer exists.", null, false));
                continue;
            }

            if (record.State != QuarantineState.Quarantined)
            {
                outcomes.Add(new QuarantineOutcome(record.FileId, false,
                    $"Only quarantined files can be restored; this entry is {Describe(record.State)}.", recordId, record.CrossVolume));
                continue;
            }

            outcomes.Add(await RestoreAsync(record, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    public async Task<QuarantineRecoveryReport> RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        var history = await _log.GetAsync(cancellationToken).ConfigureAwait(false);
        var resolved = new List<QuarantineRecord>();
        var needsAttention = new List<QuarantineRecord>();
        foreach (var record in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.State == QuarantineState.Pending)
            {
                var source = SafeInspect(record.SourcePath);
                var destination = SafeInspect(record.DestinationPath);
                if (source is { Exists: true } && destination is not { Exists: true })
                {
                    await _log.MarkFailedAsync(record.RecordId, "Interrupted before the move; the original file is still in place.",
                        cancellationToken).ConfigureAwait(false);
                }
                else if (source is not { Exists: true } && destination is { Exists: true })
                {
                    await _log.MarkQuarantinedAsync(record.RecordId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _log.MarkFailedAsync(record.RecordId,
                        source is { Exists: true }
                            ? "Both the original and the quarantine copy exist; the quarantine copy must be reviewed manually."
                            : "Neither the original nor the quarantine copy exists; manual review is required.",
                        cancellationToken).ConfigureAwait(false);
                }

                resolved.Add(record);
            }
            else if (record.State == QuarantineState.Quarantined && SafeInspect(record.DestinationPath) is not { Exists: true })
            {
                needsAttention.Add(record);
            }
        }

        return new QuarantineRecoveryReport(resolved, needsAttention);
    }

    private QuarantinePlanItem Evaluate(Guid fileId, IReadOnlyDictionary<Guid, IndexedFileEntry> index,
        string[] roots, IReadOnlySet<Guid> quarantined)
    {
        if (!index.TryGetValue(fileId, out var entry))
        {
            return Blocked(fileId, string.Empty, fileId.ToString("D"), QuarantineBlockReason.FileMissing,
                ["This file is no longer part of the index, so there is nothing safe to move."]);
        }

        var displayName = DisplayText.Sanitize(entry.RelativePath, 200);
        if (!entry.HasSafePath)
        {
            return Blocked(fileId, DisplayText.Sanitize(entry.RelativePath, 200), displayName,
                QuarantineBlockReason.PathOutsideRoot,
                ["The stored relative path does not resolve inside its scan root."]);
        }

        var inspection = SafeInspect(entry.FullPath);
        var rootAvailable = _storage.DirectoryExists(entry.RootPath);
        var otherCopies = inspection?.Exists == true && inspection.IsSymbolicLink == false
            ? CountOtherCopies(entry, index, quarantined)
            : 0;

        var facts = new QuarantineSafetyFacts(
            FileExists: inspection?.Exists == true,
            IsSymbolicLink: inspection?.IsSymbolicLink == true,
            IsInsideRoot: true,
            RootAvailable: rootAvailable,
            RootEnabled: entry.RootEnabled,
            HasContentHash: !string.IsNullOrWhiteSpace(entry.Sha256),
            OtherAvailableCopies: otherCopies);
        var verdict = QuarantineSafetyPolicy.Evaluate(facts);
        if (!verdict.IsAllowed)
        {
            return new QuarantinePlanItem(fileId, entry.FullPath, displayName, false, verdict.BlockedBy, verdict.Reasons, null, false);
        }

        var destination = ChooseDestination(entry, roots);
        var reasons = verdict.Reasons.Concat(destination.Reasons).ToArray();
        // Compared against the destination's real volume, so the UI's "spans drives" warning is truthful.
        var sourceVolume = _storage.GetVolumeRoot(entry.FullPath);
        var destinationVolume = _storage.GetVolumeRoot(destination.Directory);
        var crossVolume = sourceVolume is not null && destinationVolume is not null &&
            !string.Equals(sourceVolume, destinationVolume, StringComparison.OrdinalIgnoreCase);
        return new QuarantinePlanItem(fileId, entry.FullPath, displayName, true, null, reasons,
            Combine(destination.Directory, DestinationFileName(entry)), crossVolume);
    }

    private int CountOtherCopies(IndexedFileEntry entry, IReadOnlyDictionary<Guid, IndexedFileEntry> index, IReadOnlySet<Guid> quarantined)
    {
        if (string.IsNullOrWhiteSpace(entry.Sha256))
        {
            return 0;
        }

        var count = 0;
        foreach (var candidate in index.Values)
        {
            if (candidate.FileId == entry.FileId || quarantined.Contains(candidate.FileId) || !candidate.HasSafePath ||
                !string.Equals(candidate.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SafeInspect(candidate.FullPath) is { Exists: true, IsSymbolicLink: false })
            {
                count++;
            }
        }

        return count;
    }

    private DirectoryChoice ChooseDestination(IndexedFileEntry entry, string[] roots)
    {
        var volumeRoot = _storage.GetVolumeRoot(entry.FullPath);
        var key = VolumeKey(volumeRoot) + "-" + StableSuffix(entry.FileId);
        var choice = QuarantineDestinationPolicy.Choose(entry.FullPath, volumeRoot, roots, _options.FallbackDirectory, key);
        if (choice.SameVolume && !_storage.IsWritableDirectory(choice.Directory))
        {
            var fallback = QuarantineDestinationPolicy.Choose(entry.FullPath, null, roots, _options.FallbackDirectory, key);
            return new DirectoryChoice(fallback.Directory, fallback.SameVolume,
                ["The same-volume quarantine folder is not writable, so the per-user quarantine store is used."]);
        }

        return new DirectoryChoice(choice.Directory, choice.SameVolume, []);
    }

    private async Task<QuarantineOutcome> RestoreAsync(QuarantineRecord record, CancellationToken cancellationToken)
    {
        if (SafeInspect(record.DestinationPath) is not { Exists: true })
        {
            return new QuarantineOutcome(record.FileId, false,
                "The quarantine copy is missing, so nothing could be restored. Check the quarantine folder manually.",
                record.RecordId, record.CrossVolume);
        }

        if (SafeInspect(record.SourcePath) is { Exists: true })
        {
            return new QuarantineOutcome(record.FileId, false,
                "The original path is occupied by another file; nothing was overwritten.", record.RecordId, record.CrossVolume);
        }

        try
        {
            var directory = Path.GetDirectoryName(record.SourcePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _storage.EnsureDirectory(directory);
            }

            _storage.Move(record.DestinationPath, record.SourcePath);
            await _log.MarkRestoredAsync(record.RecordId, cancellationToken).ConfigureAwait(false);
            return new QuarantineOutcome(record.FileId, true, null, record.RecordId, record.CrossVolume);
        }
        catch (Exception exception) when (exception is StorageException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The row stays Quarantined so the user can retry the undo once the cause is fixed.
            return new QuarantineOutcome(record.FileId, false, FailureText(exception), record.RecordId, record.CrossVolume);
        }
    }

    private IReadOnlyList<Guid> ValidateSelection(IReadOnlyList<Guid> fileIds)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var selection = fileIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (selection.Length == 0)
        {
            throw new ArgumentException("Select at least one file to plan a quarantine.", nameof(fileIds));
        }

        if (selection.Length > _options.MaxItemsPerBatch)
        {
            throw new ArgumentException($"A quarantine batch may hold at most {_options.MaxItemsPerBatch} files.", nameof(fileIds));
        }

        return selection;
    }

    private async Task<IReadOnlySet<Guid>> QuarantinedFileIdsAsync(CancellationToken cancellationToken)
    {
        var quarantined = new HashSet<Guid>();
        foreach (var record in await _log.GetAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.State == QuarantineState.Quarantined)
            {
                quarantined.Add(record.FileId);
            }
        }

        return quarantined;
    }

    private StorageInspection? SafeInspect(string path)
    {
        try
        {
            return _storage.Inspect(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Never overwrites: an existing destination gets a numbered suffix, bounded so it cannot loop forever.</summary>
    private string ResolveAvailableDestination(string destination)
    {
        if (SafeInspect(destination) is not { Exists: true })
        {
            return destination;
        }

        var directory = Path.GetDirectoryName(destination) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        for (var attempt = 2; attempt <= 50; attempt++)
        {
            var candidate = Path.Combine(directory, $"{name}-{attempt}{extension}");
            if (SafeInspect(candidate) is not { Exists: true })
            {
                return candidate;
            }
        }

        throw new StorageException(StorageFailure.Conflict,
            "The quarantine folder already holds files with this name; quarantine the file manually.");
    }

    private static string DestinationFileName(IndexedFileEntry entry) =>
        DisplayText.FileName(entry.RelativePath) is { Length: > 0 } name
            ? $"{SafeStem(name)}-{StableSuffix(entry.FileId)}{SafeExtension(name)}"
            : $"{entry.FileId:D}.quarantined";

    private static string SafeStem(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length == 0 ? "file" : stem[..Math.Min(stem.Length, 80)];
    }

    private static string SafeExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length is 0 or > 16 ? string.Empty : extension;
    }

    private static string StableSuffix(Guid fileId) => fileId.ToString("N")[..8];

    private static string VolumeKey(string? volumeRoot)
    {
        var sanitized = DisplayText.Sanitize(volumeRoot, 32).Replace(':', '_').Replace('/', '_').Replace('\\', '_').Trim();
        return sanitized.Length == 0 ? "unknown" : sanitized;
    }

    private static string Combine(string directory, string leaf) =>
        Path.Combine(directory.Replace('/', Path.DirectorySeparatorChar), leaf);

    private static IReadOnlyDictionary<Guid, IndexedFileEntry> Index(IEnumerable<IndexedFileEntry> entries)
    {
        var index = new Dictionary<Guid, IndexedFileEntry>();
        foreach (var entry in entries)
        {
            index[entry.FileId] = entry;
        }

        return index;
    }

    private static QuarantinePlanItem Blocked(Guid fileId, string sourcePath, string displayName,
        QuarantineBlockReason reason, IReadOnlyList<string> reasons) =>
        new(fileId, sourcePath, displayName, false, reason, reasons, null, false);

    private static string FailureText(Exception exception) => exception switch
    {
        StorageException { Failure: StorageFailure.NotFound } =>
            "The file disappeared before it could be moved; nothing was changed.",
        StorageException { Failure: StorageFailure.AccessDenied } =>
            "Access to the file or the quarantine folder was denied; nothing was moved.",
        StorageException { Failure: StorageFailure.Conflict } =>
            "The destination already exists; nothing was overwritten.",
        StorageException { Failure: StorageFailure.Unsupported } =>
            "The file lives on a filesystem that does not support this move.",
        UnauthorizedAccessException => "Access to the file or the quarantine folder was denied; nothing was moved.",
        NotSupportedException => "The file lives on a filesystem that does not support this move.",
        _ => "The move failed for an unknown reason; the quarantine log records the attempt.",
    };

    private static string Describe(QuarantineState state) => state switch
    {
        QuarantineState.Pending => "pending (an interrupted move)",
        QuarantineState.Restored => "already restored",
        QuarantineState.Failed => "failed",
        _ => state.ToString(),
    };

    private sealed record DirectoryChoice(string Directory, bool SameVolume, IReadOnlyList<string> Reasons);
}
