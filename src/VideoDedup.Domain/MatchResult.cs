namespace VideoDedup.Domain;

/// <summary>Evidence versions a decision was based on, so persisted rows stay auditable as algorithms evolve.</summary>
public sealed record EvidenceVersions(FingerprintVersion? VisualVersion, FingerprintVersion? AudioVersion);

/// <summary>Explainable outcome of comparing two file records, including component scores and evidence versions.</summary>
public sealed record MatchResult
{
    private MatchResult(Guid fileIdA, Guid fileIdB, MatchType matchType, decimal overallScore, IReadOnlyList<string> reasons,
        decimal? visualSimilarity, decimal? audioSimilarity, decimal? audioOffsetSeconds,
        FingerprintVersion policyVersion, EvidenceVersions evidenceVersions)
    {
        FileIdA = fileIdA;
        FileIdB = fileIdB;
        MatchType = matchType;
        OverallScore = overallScore;
        Reasons = Array.AsReadOnly(reasons.ToArray());
        VisualSimilarityComponent = visualSimilarity;
        AudioSimilarityComponent = audioSimilarity;
        AudioOffsetSeconds = audioOffsetSeconds;
        PolicyVersion = policyVersion;
        EvidenceVersions = evidenceVersions;
    }

    public Guid FileIdA { get; }
    public Guid FileIdB { get; }
    public MatchType MatchType { get; }
    public decimal OverallScore { get; }
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>Component score for whole-file visual evidence when available.</summary>
    public decimal? VisualSimilarityComponent { get; }

    /// <summary>Component score for whole-file audio evidence when available.</summary>
    public decimal? AudioSimilarityComponent { get; }

    /// <summary>Detected partial-clip offset in seconds on the longer timeline.</summary>
    public decimal? AudioOffsetSeconds { get; }

    /// <summary>Version of the policy configuration that produced this decision.</summary>
    public FingerprintVersion PolicyVersion { get; }

    /// <summary>Fingerprint versions the decision consumed.</summary>
    public EvidenceVersions EvidenceVersions { get; }

    internal static MatchResult Create(Guid fileIdA, Guid fileIdB, MatchType matchType, decimal score, IEnumerable<string> reasons,
        decimal? visualSimilarity = null, decimal? audioSimilarity = null, decimal? audioOffsetSeconds = null,
        FingerprintVersion? policyVersion = null, FingerprintVersion? visualVersion = null, FingerprintVersion? audioVersion = null)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (score is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(score));
        }

        var reasonList = reasons.Select(reason => reason?.Trim())
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason!)
            .ToArray();
        if (reasonList.Length == 0)
        {
            throw new ArgumentException("A match result must explain its decision.", nameof(reasons));
        }

        return new MatchResult(fileIdA, fileIdB, matchType, score, reasonList,
            visualSimilarity, audioSimilarity, audioOffsetSeconds,
            policyVersion ?? MatchPolicyOptions.PolicyVersion,
            new EvidenceVersions(visualVersion, audioVersion));
    }
}
