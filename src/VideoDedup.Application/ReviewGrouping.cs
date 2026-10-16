using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Application;

/// <summary>Decided pairs connected into one duplicate group, with the strongest pair supplying the headline evidence.</summary>
public sealed record ReviewCluster(
    string Key,
    IReadOnlyList<Guid> FileIds,
    MatchType MatchType,
    decimal OverallScore,
    decimal? VisualScore,
    decimal? AudioScore,
    decimal? OffsetSeconds,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Pure grouping of canonical match rows into duplicate groups: files connected by any decided pair belong to the
/// same group, so a file that matches three others appears in exactly one group. Ordering is deterministic.
/// </summary>
public static class ReviewGrouping
{
    private const int MaxReasonsPerGroup = 10;

    public static IReadOnlyList<ReviewCluster> Cluster(IEnumerable<MatchPersistenceRecord> decidedPairs)
    {
        ArgumentNullException.ThrowIfNull(decidedPairs);
        var pairs = decidedPairs.Where(pair => pair is not null).ToArray();
        if (pairs.Length == 0)
        {
            return Array.Empty<ReviewCluster>();
        }

        var parent = new Dictionary<Guid, Guid>();
        Guid Find(Guid id)
        {
            var current = id;
            while (parent[current] != current)
            {
                parent[current] = parent[parent[current]];
                current = parent[current];
            }

            return current;
        }

        void Union(Guid first, Guid second)
        {
            var rootFirst = Find(first);
            var rootSecond = Find(second);
            if (rootFirst != rootSecond)
            {
                parent[rootSecond] = rootFirst;
            }
        }

        foreach (var pair in pairs)
        {
            parent[pair.FileIdA] = pair.FileIdA;
            parent[pair.FileIdB] = pair.FileIdB;
        }

        foreach (var pair in pairs)
        {
            Union(pair.FileIdA, pair.FileIdB);
        }

        var groups = new Dictionary<Guid, List<MatchPersistenceRecord>>();
        foreach (var pair in pairs)
        {
            var root = Find(pair.FileIdA);
            if (!groups.TryGetValue(root, out var rows))
            {
                groups[root] = rows = new List<MatchPersistenceRecord>();
            }

            rows.Add(pair);
        }

        return groups.Values
            .Select(ToCluster)
            .OrderByDescending(cluster => cluster.OverallScore)
            .ThenByDescending(cluster => Strength(cluster.MatchType))
            .ThenBy(cluster => cluster.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static ReviewCluster ToCluster(List<MatchPersistenceRecord> rows)
    {
        var ordered = rows.OrderBy(row => row.FileIdA.ToString("D"), StringComparer.Ordinal)
            .ThenBy(row => row.FileIdB.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var headline = ordered
            .OrderByDescending(row => Strength(row.MatchType))
            .ThenByDescending(row => row.OverallScore)
            .ThenBy(row => row.FileIdA.ToString("D"), StringComparer.Ordinal)
            .ThenBy(row => row.FileIdB.ToString("D"), StringComparer.Ordinal)
            .First();

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in ordered)
        {
            ids.Add(row.FileIdA.ToString("D"));
            ids.Add(row.FileIdB.ToString("D"));
        }

        var reasons = ordered.SelectMany(row => row.Reasons)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxReasonsPerGroup)
            .ToArray();

        return new ReviewCluster(
            string.Join('|', ids),
            ids.Select(Guid.Parse).ToArray(),
            headline.MatchType,
            headline.OverallScore,
            headline.VisualScore,
            headline.AudioScore,
            headline.AudioOffsetSeconds,
            reasons);
    }

    /// <summary>Evidence strength, which is not the enum order: an exact hash outranks every perceptual decision.</summary>
    private static int Strength(MatchType type) => type switch
    {
        MatchType.Exact => 5,
        MatchType.PartialClip => 4,
        MatchType.NearDuplicate => 3,
        MatchType.NeedsReview => 2,
        _ => 1,
    };
}
