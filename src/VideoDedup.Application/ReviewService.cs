using VideoDedup.Domain;

namespace VideoDedup.Application;

/// <summary>
/// Builds the review flow's data: decided pairs grouped into duplicate groups, each copy enriched with live
/// reachability (present, reachable root, enabled root, already quarantined) and the user's manual decision.
/// Everything here is read-only; acting on files is <see cref="IQuarantineService"/>'s job.
/// </summary>
public sealed class ReviewService(
    IMatchResultRepository matches,
    IReviewDecisionStore decisions,
    IIndexedFileCatalog catalog,
    IQuarantineLog? quarantineLog = null,
    IQuarantineStorage? storage = null,
    IQueueClock? clock = null) : IReviewService
{
    private readonly IQueueClock _clock = clock ?? SystemQueueClock.Instance;

    public async Task<IReadOnlyList<ReviewGroup>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        var clusters = ReviewGrouping.Cluster(await matches.GetAllAsync(cancellationToken).ConfigureAwait(false));
        if (clusters.Count == 0)
        {
            return Array.Empty<ReviewGroup>();
        }

        var entries = new Dictionary<Guid, IndexedFileEntry>();
        foreach (var entry in await catalog.GetEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            entries[entry.FileId] = entry;
        }

        var storedDecisions = new Dictionary<(string GroupKey, Guid FileId), ReviewDecision>();
        foreach (var record in await decisions.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.Decision != ReviewDecision.Pending)
            {
                storedDecisions[(record.GroupKey, record.FileId)] = record.Decision;
            }
        }

        var quarantined = new HashSet<Guid>();
        if (quarantineLog is not null)
        {
            foreach (var record in await quarantineLog.GetAsync(cancellationToken).ConfigureAwait(false))
            {
                if (record.State == QuarantineState.Quarantined)
                {
                    quarantined.Add(record.FileId);
                }
            }
        }

        var groups = new List<ReviewGroup>(clusters.Count);
        foreach (var cluster in clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = cluster.FileIds.Select(fileId => Describe(cluster.Key, fileId, entries, storedDecisions, quarantined)).ToArray();
            var recommendation = KeepRecommendationPolicy.Recommend(files.Select(file => new KeepCandidateFacts(
                file.FileId, file.CanBeActedOn, file.SizeBytes, file.ModifiedUtc, file.Width, file.Height, file.Duration)));
            groups.Add(ReviewGroup.Create(cluster.Key, cluster.MatchType, cluster.OverallScore, cluster.VisualScore,
                cluster.AudioScore, cluster.OffsetSeconds, cluster.Reasons, files,
                recommendation.FileId, recommendation.Reason));
        }

        return groups;
    }

    public async Task SetDecisionAsync(string groupKey, Guid fileId, ReviewDecision decision, CancellationToken cancellationToken)
    {
        var record = ReviewDecisionRecord.Create(groupKey, fileId, decision, _clock.UtcNow);
        await decisions.SaveAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private ReviewFileInfo Describe(string groupKey, Guid fileId, IReadOnlyDictionary<Guid, IndexedFileEntry> entries,
        IReadOnlyDictionary<(string GroupKey, Guid FileId), ReviewDecision> storedDecisions, IReadOnlySet<Guid> quarantined)
    {
        var isQuarantined = quarantined.Contains(fileId);
        var decision = storedDecisions.TryGetValue((groupKey, fileId), out var stored) ? stored : ReviewDecision.Pending;
        if (!entries.TryGetValue(fileId, out var entry))
        {
            // The index dropped the record (root removed). The group still renders so the user sees why nothing can be done.
            return new ReviewFileInfo(fileId, Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                HasSafePath: false, RootAvailable: false, RootEnabled: false, FilePresent: false, IsLink: false,
                IsQuarantined: isQuarantined, SizeBytes: 0, ModifiedUtc: DateTimeOffset.UnixEpoch, Duration: null,
                Width: null, Height: null, VideoCodec: null, AudioCodec: null, HasAudio: null, Sha256: null, decision);
        }

        var hasSafePath = entry.HasSafePath;
        var rootAvailable = storage is null || storage.DirectoryExists(entry.RootPath);
        var inspection = hasSafePath && storage is not null ? storage.Inspect(entry.FullPath) : null;
        var present = isQuarantined ? false : inspection?.Exists ?? !isQuarantined;
        var isLink = inspection?.IsSymbolicLink ?? false;
        return new ReviewFileInfo(entry.FileId, entry.RootId, entry.RootLabel, entry.RootPath, entry.RelativePath,
            entry.FullPath, hasSafePath, rootAvailable, entry.RootEnabled, present, isLink, isQuarantined,
            entry.SizeBytes, entry.ModifiedUtc, entry.Duration, entry.Width, entry.Height, entry.VideoCodec,
            entry.AudioCodec, entry.HasAudio, entry.Sha256, decision);
    }
}
