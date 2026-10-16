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
                ["SHA-256 content hashes are identical."],
                policyVersion: MatchPolicyOptions.PolicyVersion,
                visualVersion: CurrentOrNull(candidate.VisualEvidenceVersion, VisualAlgorithm.Version),
                audioVersion: CurrentOrNull(candidate.AudioEvidenceVersion, AudioAlgorithm.Version),
                audioOffsetSeconds: candidate.PartialAudioOffsetSeconds,
                visualSimilarity: candidate.VisualSimilarity,
                audioSimilarity: candidate.AudioSimilarity);
        }

        var (visual, audio, visualStale, audioStale) = CurrentEvidence(candidate);

        // Partial clip: short track found inside a longer one. Visual confirmation at the offset decides.
        if (candidate.PartialAudioSimilarity is decimal partialSimilarity)
        {
            var confirmation = visualStale ? null : candidate.VisualSimilarityAtOffset;
            if (partialSimilarity >= _options.PartialClipThreshold)
            {
                if (confirmation is decimal confirmed && confirmed >= _options.ConfirmationThreshold)
                {
                    var score = Math.Round((partialSimilarity + confirmed) / 2m, 4);
                    return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, MatchType.PartialClip, score,
                        [
                            $"Sliding-window audio alignment matched {partialSimilarity:P0} at offset {candidate.PartialAudioOffsetSeconds:F1} s.",
                            $"Visual content at the offset confirms with {confirmed:P0} similarity.",
                        ],
                        audioOffsetSeconds: candidate.PartialAudioOffsetSeconds,
                        policyVersion: MatchPolicyOptions.PolicyVersion,
                        visualVersion: visualVersionOf(candidate),
                        audioVersion: audioVersionOf(candidate),
                        visualSimilarity: confirmation,
                        audioSimilarity: null);
                }

                return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, MatchType.NeedsReview,
                    Math.Round(partialSimilarity, 4),
                    [
                        $"Sliding-window audio alignment matched {partialSimilarity:P0} at offset {candidate.PartialAudioOffsetSeconds:F1} s, " +
                        "but visual content at the offset is unconfirmed; treat as a potential partial clip (concatenation/excerpt) until reviewed.",
                    ],
                    audioOffsetSeconds: candidate.PartialAudioOffsetSeconds,
                    policyVersion: MatchPolicyOptions.PolicyVersion,
                    visualVersion: visualVersionOf(candidate),
                    audioVersion: audioVersionOf(candidate),
                    visualSimilarity: confirmation,
                    audioSimilarity: null);
            }
        }

        if (visual is null && audio is null)
        {
            var reason = visualStale || audioStale
                ? $"{(visualStale ? "Visual" : "Audio")} fingerprint evidence is stale for the current algorithm version."
                : (candidate.PartialAudioSimilarity is null
                    ? "No fingerprint evidence is available."
                    : "Partial alignment evidence was too weak for classification.");
            return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, MatchType.Different, 0m, [reason],
                policyVersion: MatchPolicyOptions.PolicyVersion,
                visualVersion: visualVersionOf(candidate),
                audioVersion: audioVersionOf(candidate),
                audioOffsetSeconds: candidate.PartialAudioOffsetSeconds);
        }

        if (visual is not null && audio is not null &&
            ((visual >= _options.ConflictHighThreshold && audio <= _options.ConflictLowThreshold) ||
             (audio >= _options.ConflictHighThreshold && visual <= _options.ConflictLowThreshold)))
        {
            var conflictScore = WeightedScore(visual, audio);
            return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, MatchType.NeedsReview, conflictScore,
                ["Visual and audio fingerprint evidence conflict; manual review is required."],
                visualSimilarity: visual, audioSimilarity: audio,
                policyVersion: MatchPolicyOptions.PolicyVersion,
                visualVersion: visualVersionOf(candidate), audioVersion: audioVersionOf(candidate),
                audioOffsetSeconds: candidate.PartialAudioOffsetSeconds);
        }

        var weightedScore = WeightedScore(visual, audio);
        var reasons = BuildReasons(visual, audio);
        var matchType = weightedScore >= _options.NearDuplicateThreshold
            ? MatchType.NearDuplicate
            : weightedScore >= _options.ReviewThreshold
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

        if (visualStale) reasons.Add("Stale visual fingerprint version was not used as evidence.");
        if (audioStale) reasons.Add("Stale audio fingerprint version was not used as evidence.");

        return MatchResult.Create(candidate.FileIdA, candidate.FileIdB, matchType, weightedScore, reasons,
            visualSimilarity: visual, audioSimilarity: audio,
            policyVersion: MatchPolicyOptions.PolicyVersion,
            visualVersion: CurrentOrNull(candidate.VisualEvidenceVersion, VisualAlgorithm.Version),
            audioVersion: CurrentOrNull(candidate.AudioEvidenceVersion, AudioAlgorithm.Version),
            audioOffsetSeconds: candidate.PartialAudioOffsetSeconds);
    }

    private (decimal? Visual, decimal? Audio, bool VisualStale, bool AudioStale) CurrentEvidence(MatchCandidate candidate)
    {
        var visualStale = candidate.VisualSimilarity is not null && candidate.VisualEvidenceVersion is not null &&
            candidate.VisualEvidenceVersion != VisualAlgorithm.Version;
        var audioStale = candidate.AudioSimilarity is not null && candidate.AudioEvidenceVersion is not null &&
            candidate.AudioEvidenceVersion != AudioAlgorithm.Version;
        return (visualStale ? null : candidate.VisualSimilarity, audioStale ? null : candidate.AudioSimilarity, visualStale, audioStale);
    }

    private static FingerprintVersion? CurrentOrNull(FingerprintVersion? provided, FingerprintVersion current) =>
        provided is null || provided == current ? provided : null;

    private static FingerprintVersion? visualVersionOf(MatchCandidate candidate) =>
        CurrentOrNull(candidate.VisualEvidenceVersion, VisualAlgorithm.Version);

    private static FingerprintVersion? audioVersionOf(MatchCandidate candidate) =>
        CurrentOrNull(candidate.AudioEvidenceVersion, AudioAlgorithm.Version);

    private decimal WeightedScore(decimal? visual, decimal? audio)
    {
        if (visual is null && audio is null)
        {
            return 0m;
        }

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
