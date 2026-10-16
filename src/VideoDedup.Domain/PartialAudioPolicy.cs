using System.Numerics;

namespace VideoDedup.Domain;

/// <summary>Evidence for a shorter track found inside a longer one at a specific offset.</summary>
public sealed record AudioWindowAlignment(TimeSpan Offset, decimal Similarity, TimeSpan WindowDuration, int WindowWords, int ContainerWords);

/// <summary>
/// Sliding-window audio alignment: locates the best placement of a shorter fingerprint inside a longer
/// one. Offset is expressed in container timeline seconds. The result is raw evidence only; classification
/// thresholds and visual confirmation belong to the match policy.
/// </summary>
public static class PartialAudioPolicy
{
    public static AudioWindowAlignment? BestWindowAlignment(AudioFingerprint window, AudioFingerprint container,
        PartialAudioOptions? options = null)
    {
        options ??= PartialAudioOptions.Default;
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(container);
        if (window.Version != container.Version) return null;

        var windowWords = AudioSimilarityPolicy.ParseWords(window.Data);
        var containerWords = AudioSimilarityPolicy.ParseWords(container.Data);
        if (windowWords.Count < options.MinimumWindowWords) return null;
        if (containerWords.Count <= windowWords.Count || containerWords.Count > options.MaxContainerWords) return null;

        var bestScore = 0m;
        var bestStart = -1;
        var bestDistinct = 0;
        foreach (var (start, score, distinct) in EnumerateAlignments(windowWords, containerWords, options))
        {
            // Strictly greater keeps the earliest start on ties, so results are deterministic.
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = start;
                bestDistinct = distinct;
            }
        }

        if (bestScore < options.MinimumConfidence) return null;
        if (bestDistinct < options.MinimumDistinctWords) return null;

        var wordDurationSeconds = (container.End - container.Start).TotalSeconds / containerWords.Count;
        return new AudioWindowAlignment(
            TimeSpan.FromSeconds(bestStart * wordDurationSeconds),
            Math.Round(bestScore, 4),
            window.End - window.Start,
            windowWords.Count,
            containerWords.Count);
    }

    private static IEnumerable<(int Start, decimal Score, int Distinct)> EnumerateAlignments(
        IReadOnlyList<uint> windowWords, IReadOnlyList<uint> containerWords, PartialAudioOptions options)
    {
        for (var start = 0; start <= containerWords.Count - windowWords.Count; start++)
        {
            var matched = 0;
            var distinct = 0;
            var seen = new HashSet<uint>();
            for (var index = 0; index < windowWords.Count; index++)
            {
                if (BitOperations.PopCount(windowWords[index] ^ containerWords[start + index]) > options.BitTolerance) continue;
                matched++;
                if (seen.Add(windowWords[index])) distinct++;
            }

            yield return (start, (decimal)matched / windowWords.Count, distinct);
        }
    }
}
