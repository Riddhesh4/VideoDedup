namespace VideoDedup.Domain;

/// <summary>Pure evidence-scoring and match-classification policy.</summary>
public sealed class DuplicateMatchPolicy : IDuplicateMatchPolicy
{
    private readonly MatchPolicyOptions _options;

    public DuplicateMatchPolicy(MatchPolicyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public MatchResult Evaluate(MatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Sha256A is not null && candidate.Sha256B is not null &&
            string.Equals(candidate.Sha256A, candidate.Sha256B, StringComparison.OrdinalIgnoreCase))
        {
            return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, MatchType.Exact, 1m,
                ["SHA-256 content hashes are identical."]);
        }

        var visual = candidate.VisualSimilarity;
        var audio = candidate.AudioSimilarity;
        if (visual is null && audio is null)
        {
            return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, MatchType.Different, 0m,
                ["No fingerprint evidence is available."]);
        }

        if (visual is not null && audio is not null &&
            ((visual >= _options.ConflictHighThreshold && audio <= _options.ConflictLowThreshold) ||
             (audio >= _options.ConflictHighThreshold && visual <= _options.ConflictLowThreshold)))
        {
            var conflictScore = WeightedScore(visual, audio);
            return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, MatchType.NeedsReview, conflictScore,
                ["Visual and audio fingerprint evidence conflict; manual review is required."]);
        }

        var score = WeightedScore(visual, audio);
        var reasons = BuildReasons(visual, audio);
        var matchType = score >= _options.NearDuplicateThreshold
            ? MatchType.NearDuplicate
            : score >= _options.ReviewThreshold
                ? MatchType.NeedsReview
                : MatchType.Different;

        if (matchType == MatchType.NeedsReview)
        {
            reasons.Add("Similarity is borderline; manual review is required.");
        }
        else if (matchType == MatchType.Different)
        {
            reasons.Add("Available fingerprint similarity is below the review threshold.");
        }

        return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, matchType, score, reasons);
    }

    private decimal WeightedScore(decimal? visual, decimal? audio)
    {
        if (visual is null)
        {
            return audio!.Value;
        }

        if (audio is null)
        {
            return visual.Value;
        }

        return Math.Clamp((visual.Value * _options.VisualWeight) + (audio.Value * _options.AudioWeight), 0m, 1m);
    }

    private static List<string> BuildReasons(decimal? visual, decimal? audio)
    {
        var reasons = new List<string>(2);
        if (visual is not null)
        {
            reasons.Add($"Visual fingerprint similarity: {visual.Value:P0}.");
        }

        if (audio is not null)
        {
            reasons.Add($"Audio fingerprint similarity: {audio.Value:P0}.");
        }

        return reasons;
    }
}
