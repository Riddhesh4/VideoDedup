namespace VideoDedup.Domain;

/// <summary>Evidence currently available for comparing a pair of files.</summary>
public sealed record MatchCandidate
{
    private MatchCandidate(Guid fileIdA, Guid fileIdB, string? sha256A, string? sha256B, decimal? visualSimilarity, decimal? audioSimilarity,
        decimal? visualSimilarityAtOffset, decimal? partialAudioSimilarity, decimal? partialAudioOffsetSeconds,
        FingerprintVersion? visualVersion, FingerprintVersion? audioVersion)
    {
        FileIdA = fileIdA;
        FileIdB = fileIdB;
        Sha256A = sha256A;
        Sha256B = sha256B;
        VisualSimilarity = visualSimilarity;
        AudioSimilarity = audioSimilarity;
        VisualSimilarityAtOffset = visualSimilarityAtOffset;
        PartialAudioSimilarity = partialAudioSimilarity;
        PartialAudioOffsetSeconds = partialAudioOffsetSeconds;
        VisualEvidenceVersion = visualVersion;
        AudioEvidenceVersion = audioVersion;
    }

    public Guid FileIdA { get; }
    public Guid FileIdB { get; }
    public string? Sha256A { get; }
    public string? Sha256B { get; }
    public decimal? VisualSimilarity { get; }
    public decimal? AudioSimilarity { get; }
    public decimal? PartialAudioSimilarity { get; }
    public decimal? PartialAudioOffsetSeconds { get; }
    public decimal? VisualSimilarityAtOffset { get; }
    public FingerprintVersion? VisualEvidenceVersion { get; }
    public FingerprintVersion? AudioEvidenceVersion { get; }

    public static MatchCandidate Create(
        Guid fileIdA,
        Guid fileIdB,
        string? sha256A = null,
        string? sha256B = null,
        decimal? visualSimilarity = null,
        decimal? audioSimilarity = null,
        FingerprintVersion? visualVersion = null,
        FingerprintVersion? audioVersion = null,
        decimal? visualSimilarityAtOffset = null,
        decimal? partialAudioSimilarity = null,
        decimal? partialAudioOffsetSeconds = null)
    {
        if (fileIdA == Guid.Empty || fileIdB == Guid.Empty)
        {
            throw new ArgumentException("Both file IDs must be non-empty.");
        }

        if (fileIdA == fileIdB)
        {
            throw new ArgumentException("A file cannot be compared with itself.", nameof(fileIdB));
        }

        if (visualVersion is not null && (visualVersion.Value.Major <= 0 || visualVersion.Value.Minor < 0))
        {
            throw new ArgumentException("Fingerprint version must be a valid positive version.", nameof(visualVersion));
        }

        if (audioVersion is not null && (audioVersion.Value.Major <= 0 || audioVersion.Value.Minor < 0))
        {
            throw new ArgumentException("Fingerprint version must be a valid positive version.", nameof(audioVersion));
        }

        if ((partialAudioSimilarity is null) != (partialAudioOffsetSeconds is null))
        {
            throw new ArgumentException("Partial alignment evidence requires similarity and offset together.");
        }

        return new MatchCandidate(
            fileIdA,
            fileIdB,
            NormalizeHash(sha256A, nameof(sha256A)),
            NormalizeHash(sha256B, nameof(sha256B)),
            NormalizeSimilarity(visualSimilarity, nameof(visualSimilarity)),
            NormalizeSimilarity(audioSimilarity, nameof(audioSimilarity)),
            NormalizeSimilarity(visualSimilarityAtOffset, nameof(visualSimilarityAtOffset)),
            NormalizeSimilarity(partialAudioSimilarity, nameof(partialAudioSimilarity)),
            NormalizeOffset(partialAudioOffsetSeconds, nameof(partialAudioOffsetSeconds)),
            visualVersion,
            audioVersion);
    }

    private static string? NormalizeHash(string? hash, string parameterName)
    {
        if (hash is null)
        {
            return null;
        }

        var normalized = hash.Trim();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", parameterName);
        }

        return normalized.ToUpperInvariant();
    }

    private static decimal? NormalizeSimilarity(decimal? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Similarity must be in [0, 1].");
        }

        return value;
    }

    private static decimal? NormalizeOffset(decimal? offsetSeconds, string parameterName)
    {
        if (offsetSeconds is null)
        {
            return null;
        }

        if (offsetSeconds < 0m || offsetSeconds > (decimal)FrameSamplingPolicy.DurationLimit.TotalSeconds)
        {
            throw new ArgumentOutOfRangeException(parameterName, offsetSeconds,
                "Partial offset must be within the supported duration range.");
        }

        return offsetSeconds;
    }
}
