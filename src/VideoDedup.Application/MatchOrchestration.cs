using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Application;

/// <summary>
/// Enumerates pairs across every indexed root, computes aligned signal evidence through the pure Domain
/// policies, classifies pairs, and persists every actionable decision (never <see cref="MatchType.Different"/>,
/// so persisted rows always carry fingerprint evidence).
/// </summary>
public sealed class MatchOrchestrator(IMatchEvidenceSource evidenceSource, IMatchResultRepository repository,
    IDuplicateMatchPolicy policy) : IMatchOrchestrator
{
    public async Task<IReadOnlyList<MatchPersistenceRecord>> MatchAllAsync(CancellationToken cancellationToken) =>
        await MatchCoreAsync(onlyFileId: null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<MatchPersistenceRecord>> MatchFileAsync(Guid fileId, CancellationToken cancellationToken) =>
        await MatchCoreAsync(fileId, cancellationToken).ConfigureAwait(false);

    public Task<MatchReadiness> GetReadinessAsync(Guid fileId, CancellationToken cancellationToken)
    {
        if (fileId == Guid.Empty)
        {
            throw new ArgumentException("A file id is required.", nameof(fileId));
        }

        return evidenceSource.GetReadinessAsync(fileId, cancellationToken);
    }

    private async Task<IReadOnlyList<MatchPersistenceRecord>> MatchCoreAsync(Guid? onlyFileId, CancellationToken cancellationToken)
    {
        var evidence = (await evidenceSource.GetEvidenceAsync(cancellationToken).ConfigureAwait(false))
            // Deterministic ordering makes pair enumeration and persisted-row order reproducible.
            .OrderBy(entry => entry.FileId)
            .ToArray();

        var results = new List<MatchPersistenceRecord>();
        for (var index = 0; index < evidence.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateEvidence = evidence[index];
            if (!candidateEvidence.IsReady)
            {
                // A single-file job is work that was asked for explicitly: if its fingerprints are not written yet
                // this must be retried, never silently reported as done. Losing it here is how a first scan finds no
                // duplicates at all, because Match jobs are enqueued while the fingerprint workers are still running.
                // A whole-index pass is different: unready files are simply not comparable yet and stay excluded.
                if (onlyFileId == candidateEvidence.FileId)
                {
                    throw new MediaProcessingException(MediaFailure.EvidenceNotReady);
                }

                continue;
            }

            if (onlyFileId is { } target && candidateEvidence.FileId != target) continue;
            var fromIndex = onlyFileId is null ? index + 1 : 0;
            for (var other = fromIndex; other < evidence.Length; other++)
            {
                var comparedEvidence = evidence[other];
                if (!comparedEvidence.IsReady) continue;
                if (onlyFileId is not null && comparedEvidence.FileId == onlyFileId) continue;

                var candidate = BuildCandidate(candidateEvidence, comparedEvidence);
                if (candidate is null) continue;
                var decision = policy.Evaluate(candidate);
                if (decision.MatchType == MatchType.Different) continue;
                var pair = MatchPairExtensions.CanonicalPair(decision.FileIdA, decision.FileIdB);
                var row = MatchPersistenceRecord.Create(pair.FileIdA, pair.FileIdB, decision.MatchType,
                    decision.OverallScore, decision.EvidenceVersions, decision.PolicyVersion,
                    decision.Reasons.ToArray(), decision.AudioOffsetSeconds,
                    decision.VisualSimilarityComponent, decision.AudioSimilarityComponent);
                await repository.SaveAsync(row, cancellationToken).ConfigureAwait(false);
                results.Add(row);
            }
        }

        return results;
    }

    /// <summary>Builds evidence for one pair; returns null when no usable fingerprint signal exists.</summary>
    private static MatchCandidate? BuildCandidate(FileMatchEvidence first, FileMatchEvidence second)
    {
        var visualFirst = UsableFrames(first);
        var visualSecond = UsableFrames(second);
        var audioFirst = first.Audio;
        var audioSecond = second.Audio;

        FingerprintVersion? visualVersion = visualFirst.Count >= 3 && visualSecond.Count >= 3 ? VisualAlgorithm.Version : null;
        FingerprintVersion? audioVersion = audioFirst is not null && audioSecond is not null ? AudioAlgorithm.Version : null;
        if (visualVersion is null && audioVersion is null &&
            (first.Sha256 is null || second.Sha256 is null)) return null;

        var visualSimilarity = visualVersion is null
            ? null
            : VideoSequenceSimilarityPolicy.Similarity(visualFirst, visualSecond);
        var audioSimilarity = audioVersion is null ? null : AudioSimilarityPolicy.Similarity(audioFirst!, audioSecond!);

        // Partial evidence: shorter fingerprint window inside the longer container, with visual confirmation.
        decimal? partialSimilarity = null;
        decimal? partialOffset = null;
        decimal? visualAtOffset = null;
        if (audioVersion is not null)
        {
            var shorterAudio = audioFirst!;
            var longerAudio = audioSecond!;
            var shorterFrames = visualFirst;
            var longerFrames = visualSecond;
            var shorterWords = AudioSimilarityPolicy.ParseWords(shorterAudio.Data).Count;
            var longerWords = AudioSimilarityPolicy.ParseWords(longerAudio.Data).Count;
            if (shorterWords > longerWords)
            {
                (shorterAudio, longerAudio) = (longerAudio, shorterAudio);
                (shorterFrames, longerFrames) = (longerFrames, shorterFrames);
            }

            var alignment = PartialAudioPolicy.BestWindowAlignment(shorterAudio, longerAudio);
            if (alignment is { } found)
            {
                partialSimilarity = found.Similarity;
                partialOffset = Convert.ToDecimal(found.Offset.TotalSeconds);
                visualAtOffset = VideoSequenceSimilarityPolicy.SimilarityAtOffset(shorterFrames, longerFrames, found.Offset);
            }
        }

        var hasSignal = visualSimilarity is not null || audioSimilarity is not null || partialSimilarity is not null;
        if (!hasSignal && first.Sha256 is null && second.Sha256 is null) return null;
        if (!hasSignal && !string.Equals(first.Sha256, second.Sha256, StringComparison.OrdinalIgnoreCase)) return null;

        return MatchCandidate.Create(first.FileId, second.FileId, first.Sha256, second.Sha256,
            visualSimilarity: visualSimilarity, audioSimilarity: audioSimilarity,
            visualVersion: visualVersion, audioVersion: audioVersion,
            visualSimilarityAtOffset: visualAtOffset,
            partialAudioSimilarity: partialSimilarity, partialAudioOffsetSeconds: partialOffset);
    }

    private static IReadOnlyList<VisualFingerprint> UsableFrames(FileMatchEvidence evidence) =>
        evidence.VisualFrames.Where(frame =>
                frame.Disposition == FrameDisposition.Usable && frame.Version == VisualAlgorithm.Version)
            .ToArray();
}
