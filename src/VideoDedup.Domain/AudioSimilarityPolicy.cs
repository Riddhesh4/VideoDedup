using System.Numerics;

namespace VideoDedup.Domain;

/// <summary>
/// Whole-file audio similarity over little-endian 32-bit fingerprint words (chromaprint's native word
/// layout) using Hamming distance with a small index-offset window. Coverage and distinct-word gates
/// reject shared intros, silences and repeated themes, so silence can never become candidate evidence.
/// </summary>
public static class AudioSimilarityPolicy
{
    /// <summary>Decodes fingerprint payload bytes into 32-bit words; malformed payloads yield no words.</summary>
    public static IReadOnlyList<uint> ParseWords(ReadOnlySpan<byte> data)
    {
        var words = new List<uint>(data.Length / 4);
        for (var index = 0; index + 4 <= data.Length; index += 4)
            words.Add(BitConverter.ToUInt32(data.Slice(index, 4)));
        return words;
    }

    /// <returns>Aligned similarity in [0, 1], or null when the fingerprints cannot produce usable evidence.</returns>
    public static decimal? Similarity(AudioFingerprint first, AudioFingerprint second, AudioMatchOptions? options = null)
    {
        options ??= AudioMatchOptions.Default;
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Version != second.Version) return null;

        var wordsFirst = ParseWords(first.Data);
        var wordsSecond = ParseWords(second.Data);
        if (wordsFirst.Count < options.MinimumWords || wordsSecond.Count < options.MinimumWords) return null;

        var minLength = Math.Min(wordsFirst.Count, wordsSecond.Count);
        var commonFloor = (int)Math.Ceiling(options.CoverageFloor * minLength);
        var bestScore = 0m;
        HashSet<uint>? bestDistinctFirst = null;
        HashSet<uint>? bestDistinctSecond = null;

        for (var offset = -options.MaxWordOffset; offset <= options.MaxWordOffset; offset++)
        {
            var common = 0;
            var matched = 0;
            var distinctFirst = new HashSet<uint>();
            var distinctSecond = new HashSet<uint>();
            for (var index = 0; index < minLength; index++)
            {
                var other = index + offset;
                if (other < 0 || other >= wordsSecond.Count) continue;
                common++;
                if (BitOperations.PopCount(wordsFirst[index] ^ wordsSecond[other]) > options.BitTolerance) continue;
                matched++;
                distinctFirst.Add(wordsFirst[index]);
                distinctSecond.Add(wordsSecond[other]);
            }

            if (common == 0) continue;
            var score = (decimal)matched / common;
            if (score > bestScore || (score == bestScore && bestDistinctFirst is null))
            {
                bestScore = score;
                bestDistinctFirst = distinctFirst;
                bestDistinctSecond = distinctSecond;
            }
        }

        if (bestScore > 0m && (bestDistinctFirst!.Count < options.MinimumDistinctWords || bestDistinctSecond!.Count < options.MinimumDistinctWords))
            return null;
        return Math.Round(bestScore, 4);
    }
}
