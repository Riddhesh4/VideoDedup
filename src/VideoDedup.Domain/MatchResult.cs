namespace VideoDedup.Domain;

/// <summary>Explainable outcome of comparing two file records.</summary>
public sealed record MatchResult
{
    private MatchResult(Guid fileIdA, Guid fileIdB, MatchType matchType, decimal overallScore, IReadOnlyList<string> reasons)
    {
        FileIdA = fileIdA;
        FileIdB = fileIdB;
        MatchType = matchType;
        OverallScore = overallScore;
        Reasons = Array.AsReadOnly(reasons.ToArray());
    }

    public Guid FileIdA { get; }
    public Guid FileIdB { get; }
    public MatchType MatchType { get; }
    public decimal OverallScore { get; }
    public IReadOnlyList<string> Reasons { get; }

    internal static MatchResult Create(Guid fileIdA, Guid fileIdB, MatchType matchType, decimal score, IEnumerable<string> reasons)
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

        return new MatchResult(fileIdA, fileIdB, matchType, score, reasonList);
    }
}
