using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Application;

/// <summary>Manual decision a user can attach to one file inside one duplicate group.</summary>
public enum ReviewDecision
{
    /// <summary>No decision yet; the group still needs a human.</summary>
    Pending,
    Keep,
    Reject,
    NeedsReview,
}

/// <summary>One indexed file with everything the review UI shows. Paths stay relative until joined to their root.</summary>
public sealed record IndexedFileEntry(
    Guid FileId,
    Guid RootId,
    string RootLabel,
    string RootPath,
    bool RootEnabled,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    string? Sha256 = null,
    TimeSpan? Duration = null,
    int? Width = null,
    int? Height = null,
    string? VideoCodec = null,
    string? AudioCodec = null,
    bool? HasAudio = null,
    string? Container = null)
{
    /// <summary>True when the stored relative path resolves safely inside its root.</summary>
    public bool HasSafePath => IndexedPath.TryCombine(RootPath, RelativePath, out _);

    /// <summary>Absolute path, or the sanitized relative path when the record is not safe to resolve.</summary>
    public string FullPath => IndexedPath.TryCombine(RootPath, RelativePath, out var full)
        ? full
        : DisplayText.Sanitize(RelativePath, 512);

    public FileRevision Revision => new(SizeBytes, ModifiedUtc);
}

/// <summary>Read-only lookup of every indexed file across all configured roots.</summary>
public interface IIndexedFileCatalog
{
    Task<IReadOnlyList<IndexedFileEntry>> GetEntriesAsync(CancellationToken cancellationToken);
}

/// <summary>One copy inside a duplicate group, enriched with live reachability and the user's decision.</summary>
public sealed record ReviewFileInfo(
    Guid FileId,
    Guid RootId,
    string RootLabel,
    string RootPath,
    string RelativePath,
    string FullPath,
    bool HasSafePath,
    bool RootAvailable,
    bool RootEnabled,
    bool FilePresent,
    bool IsLink,
    bool IsQuarantined,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    TimeSpan? Duration,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    bool? HasAudio,
    string? Sha256,
    ReviewDecision Decision)
{
    /// <summary>True when every safety precondition for quarantining this copy is currently satisfied.</summary>
    public bool CanBeActedOn => HasSafePath && RootAvailable && RootEnabled && FilePresent && !IsLink && !IsQuarantined;

    public string DecisionText => Decision switch
    {
        ReviewDecision.Keep => "Keep",
        ReviewDecision.Reject => "Reject",
        ReviewDecision.NeedsReview => "Needs review",
        _ => "Undecided",
    };
}

/// <summary>A duplicate group: every indexed copy that the match engine connected, with its headline evidence.</summary>
public sealed record ReviewGroup
{
    private ReviewGroup(string key, MatchType matchType, decimal overallScore, decimal? visualScore, decimal? audioScore,
        decimal? offsetSeconds, IReadOnlyList<string> reasons, IReadOnlyList<ReviewFileInfo> files,
        Guid? recommendedKeepFileId, string recommendationReason)
    {
        Key = key;
        MatchType = matchType;
        OverallScore = overallScore;
        VisualScore = visualScore;
        AudioScore = audioScore;
        OffsetSeconds = offsetSeconds;
        Reasons = Array.AsReadOnly(reasons.ToArray());
        Files = Array.AsReadOnly(files.ToArray());
        RecommendedKeepFileId = recommendedKeepFileId;
        RecommendationReason = recommendationReason;
    }

    public string Key { get; }
    public MatchType MatchType { get; }
    public decimal OverallScore { get; }
    public decimal? VisualScore { get; }
    public decimal? AudioScore { get; }
    public decimal? OffsetSeconds { get; }
    public IReadOnlyList<string> Reasons { get; }
    public IReadOnlyList<ReviewFileInfo> Files { get; }
    public Guid? RecommendedKeepFileId { get; }
    public string RecommendationReason { get; }

    public bool RequiresReview => MatchType == MatchType.NeedsReview;
    public bool IsUndecided => Files.Any(file => file.Decision == ReviewDecision.Pending);
    public string MatchTypeText => MatchType switch
    {
        MatchType.Exact => "Exact duplicate",
        MatchType.NearDuplicate => "Near duplicate",
        MatchType.PartialClip => "Partial clip",
        _ => "Needs review",
    };

    public static ReviewGroup Create(string key, MatchType matchType, decimal overallScore, decimal? visualScore,
        decimal? audioScore, decimal? offsetSeconds, IEnumerable<string> reasons, IEnumerable<ReviewFileInfo> files,
        Guid? recommendedKeepFileId, string recommendationReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentNullException.ThrowIfNull(files);
        if (!Enum.IsDefined(matchType))
        {
            throw new ArgumentOutOfRangeException(nameof(matchType));
        }

        if (overallScore is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(overallScore));
        }

        var fileList = files.ToArray();
        if (fileList.Length < 2 || fileList.Any(file => file is null))
        {
            throw new ArgumentException("A review group needs at least two copies.", nameof(files));
        }

        return new ReviewGroup(key, matchType, overallScore, visualScore, audioScore, offsetSeconds,
            reasons.ToArray(), fileList, recommendedKeepFileId, recommendationReason);
    }
}

/// <summary>Read/write port for the manual decisions shown in the review flow.</summary>
public interface IReviewService
{
    Task<IReadOnlyList<ReviewGroup>> GetGroupsAsync(CancellationToken cancellationToken);

    Task SetDecisionAsync(string groupKey, Guid fileId, ReviewDecision decision, CancellationToken cancellationToken);
}

/// <summary>Durable manual decision for one file inside one duplicate group.</summary>
public sealed record ReviewDecisionRecord
{
    private ReviewDecisionRecord(string groupKey, Guid fileId, ReviewDecision decision, DateTimeOffset recordedUtc)
    {
        GroupKey = groupKey;
        FileId = fileId;
        Decision = decision;
        RecordedUtc = recordedUtc;
    }

    public string GroupKey { get; }
    public Guid FileId { get; }
    public ReviewDecision Decision { get; }
    public DateTimeOffset RecordedUtc { get; }

    public static ReviewDecisionRecord Create(string groupKey, Guid fileId, ReviewDecision decision, DateTimeOffset recordedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);
        if (groupKey.Length > 512 || groupKey.Any(char.IsControl))
        {
            throw new ArgumentException("Group keys cannot contain control characters and must be short.", nameof(groupKey));
        }

        if (fileId == Guid.Empty)
        {
            throw new ArgumentException("A review decision needs a file ID.", nameof(fileId));
        }

        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        return new ReviewDecisionRecord(groupKey, fileId, decision, recordedUtc.ToUniversalTime());
    }
}

/// <summary>Persistence port for manual review decisions.</summary>
public interface IReviewDecisionStore
{
    Task SaveAsync(ReviewDecisionRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewDecisionRecord>> GetAllAsync(CancellationToken cancellationToken);
}
