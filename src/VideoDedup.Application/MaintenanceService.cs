using System.Security;
using System.Text;
using System.Text.Json;
using VideoDedup.Domain;

namespace VideoDedup.Application;

/// <summary>
/// Owns the user-visible privacy surface: what the application stores, exporting the index (with or without paths),
/// and deleting the index. Deletion is confirmation-gated, refused while work is in flight, and restricted by
/// <see cref="AppDataRetentionPolicy"/> to application data that can be regenerated: source media, quarantined copies
/// and scan-root settings are never touched.
/// </summary>
public sealed class MaintenanceService : IMaintenanceService
{
    private readonly IAppDataStore _store;
    private readonly IAppIndexSnapshotSource _index;
    private readonly IJobQueue _queue;
    private readonly IProcessingLog _log;
    private readonly IQueueClock _clock;
    private readonly ProcessingMetrics? _metrics;
    private readonly IReadOnlyList<string> _startupNotes;

    public MaintenanceService(IAppDataStore store, IAppIndexSnapshotSource index, IJobQueue queue,
        IProcessingLog? log = null, ProcessingMetrics? metrics = null, IEnumerable<string>? startupNotes = null,
        IQueueClock? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _log = log ?? NullProcessingLog.Instance;
        _metrics = metrics;
        _clock = clock ?? SystemQueueClock.Instance;
        _startupNotes = startupNotes?.Where(note => !string.IsNullOrWhiteSpace(note)).Select(note => PrivacyText.Sanitize(note)).ToArray() ?? [];
    }

    public async Task<MaintenanceReport> GetReportAsync(CancellationToken cancellationToken)
    {
        var entries = _store.Enumerate();
        var roots = await _index.GetScanRootPathsAsync(cancellationToken).ConfigureAwait(false);
        var decisions = AppDataRetentionPolicy.ClassifyAll(_store.DataRoot, entries, roots);
        var areas = decisions.GroupBy(decision => ToArea(decision.Category))
            .OrderBy(group => group.Key)
            .Select(group => new AppDataAreaUsage(group.Key, group.Sum(decision => Size(entries, decision.FullPath)),
                group.Count(), group.Where(decision => decision.Deletable).Sum(decision => Size(entries, decision.FullPath)),
                group.Count(decision => decision.Deletable)))
            .ToArray();
        var quarantine = decisions.Where(decision => decision.Category == AppDataCategory.QuarantineStore).ToArray();
        return new MaintenanceReport(
            _store.DataRoot,
            areas,
            areas.Sum(area => area.SizeBytes),
            areas.Sum(area => area.ReclaimableBytes),
            areas.Sum(area => area.ReclaimableFileCount),
            quarantine.Length,
            quarantine.Sum(decision => Size(entries, decision.FullPath)),
            BuildNotes(decisions, quarantine));
    }

    public async Task<IndexExportResult> ExportIndexAsync(IndexExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DestinationPath) || !Path.IsPathFullyQualified(request.DestinationPath))
        {
            throw new ArgumentException("Choose an absolute export path.", nameof(request));
        }

        if (AppDataRetentionPolicy.IsInside(_store.DataRoot, request.DestinationPath))
        {
            throw new InvalidOperationException("Choose an export location outside the application data folder.");
        }

        var snapshot = await _index.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var json = IndexExportDocument.Write(snapshot, request.IncludePaths, _clock.UtcNow);
        _store.WriteText(request.DestinationPath, json);
        return new IndexExportResult(request.DestinationPath, Encoding.UTF8.GetByteCount(json), request.IncludePaths,
            snapshot.Roots.Count, snapshot.Files.Count, snapshot.Matches.Count, snapshot.Quarantine.Count);
    }

    public async Task<IndexDeletionResult> DeleteIndexAsync(bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return IndexDeletionResult.NotConfirmed;
        }

        var stats = await _queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        if (stats.Queued > 0 || stats.Leased > 0)
        {
            throw new InvalidOperationException(
                "Work is still queued or in progress. Let it finish, or cancel the scan, before deleting the index.");
        }

        var roots = await _index.GetScanRootPathsAsync(cancellationToken).ConfigureAwait(false);
        var decisions = AppDataRetentionPolicy.ClassifyAll(_store.DataRoot, _store.Enumerate(), roots);
        var deleted = new List<string>();
        var retained = new List<AppDataDecision>();
        var failures = new List<string>();
        foreach (var decision in decisions)
        {
            if (!decision.Deletable)
            {
                retained.Add(decision);
                continue;
            }

            try
            {
                _store.DeleteFile(decision.FullPath);
                deleted.Add(decision.FullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
            {
                failures.Add($"{PrivacyText.RedactPaths(decision.FullPath)}: {PrivacyText.RedactPaths(exception.Message)}");
            }
        }

        if (deleted.Count > 0)
        {
            try
            {
                _store.DeleteEmptyDirectories();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
            {
                failures.Add(PrivacyText.RedactPaths(exception.Message));
            }
        }

        _log.Write(ProcessingLogEvent.Create("Maintenance",
            deleted.Count > 0 ? ProcessingOutcome.Processed : ProcessingOutcome.Idle, TimeSpan.Zero,
            failures.Count == 0 ? "IndexDeleted" : "IndexDeletePartial", _clock));
        return new IndexDeletionResult(true, deleted, retained, failures);
    }

    private static AppDataArea ToArea(AppDataCategory category) => category switch
    {
        AppDataCategory.Index => AppDataArea.IndexDatabase,
        AppDataCategory.DiscoveryIndex => AppDataArea.DiscoverySnapshots,
        AppDataCategory.Configuration => AppDataArea.Configuration,
        AppDataCategory.Logs => AppDataArea.ProcessingLogs,
        AppDataCategory.QuarantineStore => AppDataArea.QuarantineStore,
        _ => AppDataArea.Unrecognized,
    };

    private static long Size(IReadOnlyList<AppDataEntry> entries, string path) =>
        entries.FirstOrDefault(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase))?.SizeBytes ?? 0;

    private IReadOnlyList<string> BuildNotes(IReadOnlyList<AppDataDecision> decisions, IReadOnlyList<AppDataDecision> quarantine)
    {
        var notes = new List<string>(_startupNotes);
        if (quarantine.Count > 0)
        {
            notes.Add($"{quarantine.Count} quarantined file(s) stay on disk; restore or remove them in the review tab first.");
        }

        if (decisions.Any(decision => decision.Category == AppDataCategory.Configuration))
        {
            notes.Add(AppDataRetentionPolicy.ConfigurationRetainedReason + ".");
        }

        if (decisions.Any(decision => decision.Reason == AppDataRetentionPolicy.InsideScanRootReason))
        {
            notes.Add("Part of the data folder is inside a scanned folder, so it is never deleted automatically.");
        }

        if (_metrics is not null)
        {
            var snapshot = _metrics.Snapshot();
            if (snapshot.JobsProcessed > 0)
            {
                notes.Add($"Processed {snapshot.JobsProcessed} job(s) in the last {snapshot.Window.TotalMinutes:0} minute(s) ({snapshot.JobsPerMinute:0.0}/min).");
            }
        }

        return notes;
    }
}

/// <summary>
/// Writes the export document. Paths are either included verbatim (the user's explicit choice) or replaced by a
/// null path plus a "pathRedacted" marker, so a diagnostics file can be shared without revealing file names.
/// </summary>
public static class IndexExportDocument
{
    public const int SchemaVersion = 1;

    public static string Write(AppIndexSnapshot snapshot, bool includePaths, DateTimeOffset generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("generatedUtc", generatedUtc.ToUniversalTime());
            writer.WriteBoolean("includesPaths", includePaths);
            WriteRoots(writer, snapshot.Roots, includePaths);
            WriteFiles(writer, snapshot.Files, includePaths);
            WriteProcessing(writer, snapshot.Processing);
            WriteFingerprints(writer, snapshot.Visual, snapshot.Audio);
            WriteMatches(writer, snapshot.Matches);
            WriteDecisions(writer, snapshot.Decisions);
            WriteQuarantine(writer, snapshot.Quarantine, includePaths);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRoots(Utf8JsonWriter writer, IReadOnlyList<ScanRoot> roots, bool includePaths)
    {
        writer.WriteStartArray("roots");
        foreach (var root in roots)
        {
            writer.WriteStartObject();
            writer.WriteString("id", root.Id);
            writer.WriteString("label", PrivacyText.SanitizeOptional(root.Label));
            WritePath(writer, root.Path, includePaths);
            writer.WriteString("type", root.RootType.ToString());
            writer.WriteBoolean("enabled", root.Enabled);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteFiles(Utf8JsonWriter writer, IReadOnlyList<IndexedFile> files, bool includePaths)
    {
        writer.WriteStartArray("files");
        foreach (var file in files)
        {
            writer.WriteStartObject();
            writer.WriteString("id", file.Id);
            writer.WriteString("rootId", file.ScanRootId);
            WritePath(writer, file.RelativePath, includePaths);
            writer.WriteNumber("sizeBytes", file.SizeBytes);
            writer.WriteString("modifiedUtc", file.ModifiedUtc.ToUniversalTime());
            writer.WriteString("scanState", file.ScanState.ToString());
            WriteNumber(writer, "durationSeconds", file.Duration?.TotalSeconds);
            WriteNumber(writer, "width", file.Width);
            WriteNumber(writer, "height", file.Height);
            writer.WriteString("videoCodec", PrivacyText.SanitizeOptional(file.VideoCodec));
            writer.WriteString("audioCodec", PrivacyText.SanitizeOptional(file.AudioCodec));
            WriteBoolean(writer, "hasAudio", file.HasAudio);
            writer.WriteString("sha256", PrivacyText.SanitizeOptional(file.Sha256));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteProcessing(Utf8JsonWriter writer, IReadOnlyList<MediaProcessingRecord> processing)
    {
        writer.WriteStartArray("processing");
        foreach (var record in processing)
        {
            writer.WriteStartObject();
            writer.WriteString("fileId", record.FileId);
            writer.WriteNumber("revisionSizeBytes", record.Revision.SizeBytes);
            writer.WriteString("revisionModifiedUtc", record.Revision.ModifiedUtc.ToUniversalTime());
            writer.WriteString("sha256", PrivacyText.SanitizeOptional(record.Sha256));
            WriteNumber(writer, "durationSeconds", record.Metadata?.DurationSeconds);
            WriteNumber(writer, "width", record.Metadata?.Width);
            WriteNumber(writer, "height", record.Metadata?.Height);
            WriteNumber(writer, "bitDepth", record.Metadata?.BitDepth);
            WriteNumber(writer, "frameRate", record.Metadata?.FrameRate);
            writer.WriteString("container", PrivacyText.SanitizeOptional(record.Metadata?.Container));
            writer.WriteString("failure", record.Failure?.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteFingerprints(Utf8JsonWriter writer, IReadOnlyList<VisualProcessingRecord> visual,
        IReadOnlyList<AudioProcessingRecord> audio)
    {
        writer.WriteStartArray("fingerprints");
        foreach (var record in visual)
        {
            writer.WriteStartObject();
            writer.WriteString("fileId", record.FileId);
            writer.WriteString("kind", "visual");
            writer.WriteString("algorithm", PrivacyText.SanitizeOptional(record.Algorithm));
            writer.WriteString("version", Version(record.Version));
            if (record.Frames.Count > 0)
            {
                writer.WriteString("frameVersion", Version(record.Frames[0].Version));
            }

            writer.WriteNumber("frameCount", record.Frames.Count);
            writer.WriteNumber("usableFrameCount", record.Frames.Count(frame => frame.Disposition == FrameDisposition.Usable));
            writer.WriteString("failure", record.Failure?.ToString());
            writer.WriteEndObject();
        }

        foreach (var record in audio)
        {
            writer.WriteStartObject();
            writer.WriteString("fileId", record.FileId);
            writer.WriteString("kind", "audio");
            writer.WriteString("algorithm", PrivacyText.SanitizeOptional(record.Algorithm));
            writer.WriteString("version", Version(record.Version));
            writer.WriteBoolean("hasAudio", record.HasAudio);
            writer.WriteNumber("fingerprintBytes", record.Fingerprint.Length);
            writer.WriteNumber("durationSeconds", record.Duration.TotalSeconds);
            writer.WriteString("failure", record.Failure?.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMatches(Utf8JsonWriter writer, IReadOnlyList<MatchPersistenceRecord> matches)
    {
        writer.WriteStartArray("matches");
        foreach (var match in matches)
        {
            writer.WriteStartObject();
            writer.WriteString("fileIdA", match.FileIdA);
            writer.WriteString("fileIdB", match.FileIdB);
            writer.WriteString("matchType", match.MatchType.ToString());
            writer.WriteNumber("overallScore", match.OverallScore);
            WriteNumber(writer, "visualScore", match.VisualScore);
            WriteNumber(writer, "audioScore", match.AudioScore);
            WriteNumber(writer, "audioOffsetSeconds", match.AudioOffsetSeconds);
            writer.WriteString("policyVersion", Version(match.PolicyVersion));
            writer.WriteString("visualVersion", match.EvidenceVersions.VisualVersion is { } visual ? Version(visual) : null);
            writer.WriteString("audioVersion", match.EvidenceVersions.AudioVersion is { } audio ? Version(audio) : null);
            writer.WriteStartArray("reasons");
            foreach (var reason in match.Reasons)
            {
                writer.WriteStringValue(PrivacyText.RedactPaths(reason));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteDecisions(Utf8JsonWriter writer, IReadOnlyList<ReviewDecisionRecord> decisions)
    {
        writer.WriteStartArray("decisions");
        foreach (var decision in decisions)
        {
            writer.WriteStartObject();
            writer.WriteString("groupKey", PrivacyText.SanitizeOptional(decision.GroupKey));
            writer.WriteString("fileId", decision.FileId);
            writer.WriteString("decision", decision.Decision.ToString());
            writer.WriteString("recordedUtc", decision.RecordedUtc.ToUniversalTime());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteQuarantine(Utf8JsonWriter writer, IReadOnlyList<QuarantineRecord> quarantine, bool includePaths)
    {
        writer.WriteStartArray("quarantine");
        foreach (var record in quarantine)
        {
            writer.WriteStartObject();
            writer.WriteString("recordId", record.RecordId);
            writer.WriteString("fileId", record.FileId);
            writer.WriteString("state", record.State.ToString());
            writer.WriteBoolean("crossVolume", record.CrossVolume);
            WritePath(writer, record.SourcePath, includePaths, "sourcePath");
            WritePath(writer, record.DestinationPath, includePaths, "destinationPath");
            writer.WriteString("createdUtc", record.CreatedUtc.ToUniversalTime());
            if (record.CompletedUtc is { } completed)
            {
                writer.WriteString("completedUtc", completed.ToUniversalTime());
            }
            else
            {
                writer.WriteNull("completedUtc");
            }
            writer.WriteString("failureReason", PrivacyText.RedactPathsOptional(record.FailureReason));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WritePath(Utf8JsonWriter writer, string path, bool includePaths, string propertyName = "path")
    {
        if (includePaths)
        {
            writer.WriteString(propertyName, PrivacyText.Sanitize(path));
            return;
        }

        writer.WriteNull(propertyName);
        writer.WriteBoolean(propertyName + "Redacted", true);
    }

    private static void WriteNumber(Utf8JsonWriter writer, string propertyName, double? value)
    {
        if (value is double number && double.IsFinite(number))
        {
            writer.WriteNumber(propertyName, number);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static void WriteNumber(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(propertyName, number);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static void WriteNumber(Utf8JsonWriter writer, string propertyName, decimal? value)
    {
        if (value is decimal number)
        {
            writer.WriteNumber(propertyName, number);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static void WriteBoolean(Utf8JsonWriter writer, string propertyName, bool? value)
    {
        if (value is bool flag)
        {
            writer.WriteBoolean(propertyName, flag);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static string Version(FingerprintVersion version) => $"{version.Major}.{version.Minor}";
}
