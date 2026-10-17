using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Application;


/// <summary>One file's complete current fingerprint evidence plus completion state for matching.</summary>
public sealed record FileMatchEvidence(Guid FileId, Guid RootId, string? Sha256,
    IReadOnlyList<VisualFingerprint> VisualFrames, AudioFingerprint? Audio, bool IsReady);

/// <summary>
/// Why a file can or cannot be matched right now. "Still being fingerprinted" must be retried, while a file whose
/// processing failed will never become ready and must not be retried forever.
/// </summary>
public enum MatchReadiness
{
    /// <summary>The file is no longer indexed, so there is nothing to match.</summary>
    NotIndexed,

    /// <summary>Metadata, hash, visual or audio evidence is missing or stale and is expected to arrive.</summary>
    InProgress,

    /// <summary>Every required fingerprint is current; the file can be compared.</summary>
    Ready,

    /// <summary>A processing stage failed permanently (for example an unsupported or corrupt file).</summary>
    Unprocessable,
}

/// <summary>Gathers fully-processed files and their fingerprints for cross-root pair matching.</summary>
public interface IMatchEvidenceSource
{
    Task<IReadOnlyList<FileMatchEvidence>> GetEvidenceAsync(CancellationToken cancellationToken);

    /// <summary>Classifies one file without building the whole evidence set.</summary>
    Task<MatchReadiness> GetReadinessAsync(Guid fileId, CancellationToken cancellationToken);
}

/// <summary>Durable, canonical, cancelled-pair-safe persistence row for one decided pair.</summary>
public sealed record MatchPersistenceRecord
{
    private MatchPersistenceRecord(Guid fileIdA, Guid fileIdB, MatchType matchType, decimal overallScore,
        decimal? visualScore, decimal? audioScore, decimal? audioOffsetSeconds, FingerprintVersion policyVersion,
        EvidenceVersions evidenceVersions, string[] reasons)
    {
        FileIdA = fileIdA;
        FileIdB = fileIdB;
        MatchType = matchType;
        OverallScore = overallScore;
        VisualScore = visualScore;
        AudioScore = audioScore;
        AudioOffsetSeconds = audioOffsetSeconds;
        PolicyVersion = policyVersion;
        EvidenceVersions = evidenceVersions;
        _reasons = reasons;
    }

    /// <summary>Canonical (invariant, ordered) pair member used as the stabilized sort key.</summary>
    public Guid FileIdA { get; }
    public Guid FileIdB { get; }
    public MatchType MatchType { get; }
    public decimal OverallScore { get; }

    /// <summary>Component visual similarity the decision was based on, when visual evidence existed.</summary>
    public decimal? VisualScore { get; }

    /// <summary>Component audio similarity the decision was based on, when audio evidence existed.</summary>
    public decimal? AudioScore { get; }

    public decimal? AudioOffsetSeconds { get; }
    public FingerprintVersion PolicyVersion { get; }
    public EvidenceVersions EvidenceVersions { get; }
    private readonly string[] _reasons;

    /// <summary>Defensive copy of the explanation list.</summary>
    public IReadOnlyList<string> Reasons { get { var copy = new string[_reasons.Length]; Array.Copy(_reasons, copy, _reasons.Length); return copy; } }

    /// <summary>Creates an immutable canonical record. Pair order is normalized so (A,B) and (B,A) are one row.</summary>
    public static MatchPersistenceRecord Create(Guid fileIdA, Guid fileIdB, MatchType matchType, decimal overallScore,
        EvidenceVersions evidenceVersions, FingerprintVersion policyVersion, string[] reasons,
        decimal? audioOffsetSeconds = null, decimal? visualScore = null, decimal? audioScore = null)
    {
        if (fileIdA == Guid.Empty || fileIdB == Guid.Empty || fileIdA == fileIdB)
            throw new ArgumentException("Match record requires two distinct non-empty file IDs.");
        if (string.CompareOrdinal(fileIdA.ToString("D"), fileIdB.ToString("D")) > 0)
            (fileIdA, fileIdB) = (fileIdB, fileIdA);
        if (!Enum.IsDefined(matchType))
            throw new ArgumentOutOfRangeException(nameof(matchType));
        if (overallScore < 0m || overallScore > 1m)
            throw new ArgumentOutOfRangeException(nameof(overallScore));
        if (reasons is null || reasons.Length == 0 || reasons.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A match record must contain at least one nonblank reason.");
        if (policyVersion.Major <= 0)
            throw new ArgumentOutOfRangeException(nameof(policyVersion));
        if (audioOffsetSeconds is < 0m)
            throw new ArgumentOutOfRangeException(nameof(audioOffsetSeconds));
        if (visualScore is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(visualScore));
        if (audioScore is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(audioScore));

        return new MatchPersistenceRecord(fileIdA, fileIdB, matchType, overallScore,
            visualScore, audioScore, audioOffsetSeconds, policyVersion, evidenceVersions,
            NormalizeReasons(reasons));
    }

    private static string[] NormalizeReasons(IEnumerable<string> reasons) =>
        reasons.Select(reason => reason.Trim()).Where(reason => reason.Length > 0).ToArray();
}

/// <summary>Persistence port for decided match pairs.</summary>
public interface IMatchResultRepository
{
    Task SaveAsync(MatchPersistenceRecord record, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid fileIdA, Guid fileIdB, CancellationToken cancellationToken);

    Task<IReadOnlyList<MatchPersistenceRecord>> GetAllAsync(CancellationToken cancellationToken);
}

public sealed record MatchPairDefinition(Guid FileIdA, Guid FileIdB);

/// <summary>Pair-comparison orchestration classes use these ports; workers bridge queue jobs into them.</summary>
public interface IMatchOrchestrator
{
    Task<IReadOnlyList<MatchPersistenceRecord>> MatchAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MatchPersistenceRecord>> MatchFileAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>Whether one file can be matched yet; see <see cref="MatchReadiness"/>.</summary>
    Task<MatchReadiness> GetReadinessAsync(Guid fileId, CancellationToken cancellationToken);
}

public static class MatchPairExtensions
{
    /// <summary>Canonical ordered pair key used for deduplication and persistence.</summary>
    public static MatchPairDefinition CanonicalPair(Guid one, Guid other)
    {
        var (first, second) = string.CompareOrdinal(one.ToString("D"), other.ToString("D")) <= 0 ? (one, other) : (other, one);
        return new MatchPairDefinition(first, second);
    }
}
