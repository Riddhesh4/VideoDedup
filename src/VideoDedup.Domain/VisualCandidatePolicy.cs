using System.Numerics;

namespace VideoDedup.Domain;

/// <summary>Aligned evidence gate only; sequence alignment and final match decisions belong to Phase 7.</summary>
public static class VisualCandidatePolicy
{
    public static bool IsCandidate(IReadOnlyList<VisualFingerprint> first, IReadOnlyList<VisualFingerprint> second)
    {
        if (first.Count is < 3 or > FrameSamplingPolicy.FrameLimit || second.Count != first.Count) return false;
        var usable = first.Zip(second).Where(pair =>
            pair.First.Disposition == FrameDisposition.Usable && pair.Second.Disposition == FrameDisposition.Usable &&
            pair.First.Quality >= 0.2m && pair.Second.Quality >= 0.2m &&
            pair.First.Version == VisualAlgorithm.Version && pair.Second.Version == pair.First.Version &&
            pair.First.Timestamp == pair.Second.Timestamp).ToArray();
        // Shared intros and single distinctive frames cannot dominate the candidate signal.
        if (usable.Length < 3 || usable.Length * 4 < first.Count * 3) return false;
        var matches = usable.Where(pair => BitOperations.PopCount(pair.First.PerceptualHash ^ pair.Second.PerceptualHash) <= 10).ToArray();
        return matches.Length * 4 >= first.Count * 3 &&
            matches.Select(pair => pair.First.PerceptualHash).Distinct().Count() >= 3 &&
            matches.Select(pair => pair.Second.PerceptualHash).Distinct().Count() >= 3;
    }
}
