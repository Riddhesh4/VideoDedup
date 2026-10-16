namespace VideoDedup.Domain;

/// <summary>Quality and availability facts for one copy inside a duplicate group.</summary>
public sealed record KeepCandidateFacts(
    Guid FileId,
    bool IsAvailable,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    int? Width,
    int? Height,
    TimeSpan? Duration);

/// <summary>Advisory keep suggestion. It is never an action: the user still confirms every change.</summary>
public sealed record KeepRecommendation(Guid? FileId, string Reason)
{
    public bool HasSuggestion => FileId is not null;
}

/// <summary>
/// Deterministic advisory policy that points at the copy worth keeping: highest pixel count first (the richest
/// encode), then larger file, then newest modification, then file ID. Unreachable copies are never suggested.
/// </summary>
public static class KeepRecommendationPolicy
{
    public static KeepRecommendation Recommend(IEnumerable<KeepCandidateFacts> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var available = candidates.Where(candidate => candidate is { IsAvailable: true })
            .OrderByDescending(candidate => Pixels(candidate))
            .ThenByDescending(candidate => candidate.SizeBytes)
            .ThenByDescending(candidate => candidate.ModifiedUtc)
            .ThenBy(candidate => candidate.FileId.ToString("D"), StringComparer.Ordinal)
            .ToArray();

        if (available.Length == 0)
        {
            return new KeepRecommendation(null,
                "No copy in this group is reachable right now, so nothing can be recommended.");
        }

        var best = available[0];
        var pixels = Pixels(best);
        var reason = pixels > 0
            ? $"Highest resolution ({best.Width}x{best.Height}) with the largest payload among reachable copies."
            : "Largest reachable copy; resolution is unknown.";
        return new KeepRecommendation(best.FileId, reason);
    }

    private static long Pixels(KeepCandidateFacts candidate) =>
        candidate.Width is int width && candidate.Height is int height && width > 0 && height > 0
            ? (long)width * height
            : 0L;
}
