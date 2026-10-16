using System.Numerics;

namespace VideoDedup.Domain;

/// <summary>
/// Whole-list visual-sequence similarity over usable sampled frames.
/// Compares frames by sampled order (resilient to recording time shifts), with a small index-offset
/// window so dropped/added frames at the start of a sequence cannot misalign the comparison.
/// Single distinctive frames, black/static repeats and shared intros cannot dominate: coverage and
/// distinct-hash gates must both pass before a nonzero score is reported.
/// </summary>
public static class VideoSequenceSimilarityPolicy
{
    private readonly record struct UsableFrame(ulong Hash, TimeSpan Timestamp);

    private static IReadOnlyList<UsableFrame> Usable(IReadOnlyList<VisualFingerprint> frames, VideoMatchOptions options) =>
        frames.Where(frame => frame.Disposition == FrameDisposition.Usable &&
                frame.Version == VisualAlgorithm.Version && frame.Quality >= options.MinimumQuality)
            .Select(frame => new UsableFrame(frame.PerceptualHash, frame.Timestamp))
            .ToArray();

    /// <returns>Aligned similarity in [0, 1], or null when the sequences cannot produce usable evidence.</returns>
    public static decimal? Similarity(IReadOnlyList<VisualFingerprint> first, IReadOnlyList<VisualFingerprint> second,
        VideoMatchOptions? options = null)
    {
        options ??= VideoMatchOptions.Default;
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var usableFirst = Usable(first, options);
        var usableSecond = Usable(second, options);
        if (usableFirst.Count < options.MinimumFrames || usableSecond.Count < options.MinimumFrames) return null;

        var commonFloor = (int)Math.Ceiling(options.CoverageFloor * Math.Min(usableFirst.Count, usableSecond.Count));
        var bestScore = 0m;
        var bestCommon = 0;
        HashSet<ulong>? bestDistinctFirst = null;
        HashSet<ulong>? bestDistinctSecond = null;

        for (var offset = -options.MaxFrameOffset; offset <= options.MaxFrameOffset; offset++)
        {
            var common = 0;
            var matched = 0;
            var distinctFirst = new HashSet<ulong>();
            var distinctSecond = new HashSet<ulong>();
            for (var index = 0; index < usableFirst.Count; index++)
            {
                var other = index + offset;
                if (other < 0 || other >= usableSecond.Count) continue;
                common++;
                if (BitOperations.PopCount(usableFirst[index].Hash ^ usableSecond[other].Hash) > options.HammingTolerance) continue;
                matched++;
                distinctFirst.Add(usableFirst[index].Hash);
                distinctSecond.Add(usableSecond[other].Hash);
            }

            if (common == 0) continue;
            var score = (decimal)matched / common;
            // Strictly greater keeps the earliest offset on ties, so results are deterministic.
            if (score > bestScore || (score == bestScore && bestDistinctFirst is null))
            {
                bestScore = score;
                bestCommon = common;
                bestDistinctFirst = distinctFirst;
                bestDistinctSecond = distinctSecond;
            }
        }

        if (bestCommon < commonFloor) return null;
        if (bestScore > 0m && (bestDistinctFirst!.Count < options.MinimumDistinctFrames || bestDistinctSecond!.Count < options.MinimumDistinctFrames))
            return null;
        return Math.Round(bestScore, 4);
    }

    /// <summary>
    /// Offset confirmation: does <paramref name="window"/> content appear inside <paramref name="container"/>
    /// starting at <paramref name="offset"/> on the container timeline? Timestamp alignment with bounded tolerance.
    /// </summary>
    public static decimal? SimilarityAtOffset(IReadOnlyList<VisualFingerprint> window, IReadOnlyList<VisualFingerprint> container,
        TimeSpan offset, VideoMatchOptions? options = null)
    {
        options ??= VideoMatchOptions.Default;
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(container);
        if (offset < TimeSpan.Zero) return null;

        var usableWindow = Usable(window, options);
        var usableContainer = Usable(container, options);
        if (usableWindow.Count < options.MinimumFrames || usableContainer.Count < options.MinimumFrames) return null;
        if (offset > usableContainer[^1].Timestamp) return null;

        var matched = 0;
        var distinctWindow = new HashSet<ulong>();
        var distinctContainer = new HashSet<ulong>();
        foreach (var frame in usableWindow)
        {
            foreach (var candidate in usableContainer)
            {
                if (Math.Abs((candidate.Timestamp - offset - frame.Timestamp).Ticks) > options.TimestampTolerance.Ticks) continue;
                if (BitOperations.PopCount(frame.Hash ^ candidate.Hash) > options.HammingTolerance) continue;
                matched++;
                distinctWindow.Add(frame.Hash);
                distinctContainer.Add(candidate.Hash);
                break;
            }
        }

        var score = (decimal)matched / usableWindow.Count;
        if (score > 0m && (distinctWindow.Count < options.MinimumDistinctFrames || distinctContainer.Count < options.MinimumDistinctFrames))
            return null;
        return Math.Round(score, 4);
    }
}
