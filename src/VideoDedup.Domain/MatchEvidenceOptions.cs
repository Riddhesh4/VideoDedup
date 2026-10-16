namespace VideoDedup.Domain;

/// <summary>Configuration for aligned visual-sequence similarity. Deterministic and versioned with the policy.</summary>
public sealed record VideoMatchOptions
{
    public VideoMatchOptions(int maxFrameOffset = 2, int hammingTolerance = 10, decimal coverageFloor = 0.6m,
        int minimumFrames = 3, int minimumDistinctFrames = 3, decimal minimumQuality = 0.2m, TimeSpan? timestampTolerance = null)
    {
        if (maxFrameOffset is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(maxFrameOffset));
        if (hammingTolerance is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(hammingTolerance));
        if (coverageFloor is < 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(coverageFloor));
        if (minimumFrames is < 1) throw new ArgumentOutOfRangeException(nameof(minimumFrames));
        if (minimumDistinctFrames < 1 || minimumDistinctFrames > minimumFrames) throw new ArgumentOutOfRangeException(nameof(minimumDistinctFrames));
        if (minimumQuality is < 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(minimumQuality));
        if (timestampTolerance is not null && (timestampTolerance <= TimeSpan.Zero || timestampTolerance > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(timestampTolerance));
        MaxFrameOffset = maxFrameOffset;
        HammingTolerance = hammingTolerance;
        CoverageFloor = coverageFloor;
        MinimumFrames = minimumFrames;
        MinimumDistinctFrames = minimumDistinctFrames;
        MinimumQuality = minimumQuality;
        TimestampTolerance = timestampTolerance ?? TimeSpan.FromSeconds(1);
    }

    public int MaxFrameOffset { get; }
    public int HammingTolerance { get; }
    public decimal CoverageFloor { get; }
    public int MinimumFrames { get; }
    public int MinimumDistinctFrames { get; }
    public decimal MinimumQuality { get; }
    public TimeSpan TimestampTolerance { get; }

    public static VideoMatchOptions Default { get; } = new();
}

/// <summary>Configuration for whole-file audio word similarity.</summary>
public sealed record AudioMatchOptions
{
    public AudioMatchOptions(int bitTolerance = 4, int maxWordOffset = 2, decimal coverageFloor = 0.8m,
        int minimumWords = 8, int minimumDistinctWords = 4)
    {
        if (bitTolerance is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(bitTolerance));
        if (maxWordOffset is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(maxWordOffset));
        if (coverageFloor is < 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(coverageFloor));
        if (minimumWords is < 1) throw new ArgumentOutOfRangeException(nameof(minimumWords));
        if (minimumDistinctWords < 1 || minimumDistinctWords > minimumWords) throw new ArgumentOutOfRangeException(nameof(minimumDistinctWords));
        BitTolerance = bitTolerance;
        MaxWordOffset = maxWordOffset;
        CoverageFloor = coverageFloor;
        MinimumWords = minimumWords;
        MinimumDistinctWords = minimumDistinctWords;
    }

    public int BitTolerance { get; }
    public int MaxWordOffset { get; }
    public decimal CoverageFloor { get; }
    public int MinimumWords { get; }
    public int MinimumDistinctWords { get; }

    public static AudioMatchOptions Default { get; } = new();
}

/// <summary>Configuration for sliding-window audio alignment over a longer container fingerprint.</summary>
public sealed record PartialAudioOptions
{
    public PartialAudioOptions(int bitTolerance = 4, int minimumWindowWords = 6, int minimumDistinctWords = 3,
        decimal minimumConfidence = 0.5m, int maxContainerWords = 4096)
    {
        if (bitTolerance is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(bitTolerance));
        if (minimumWindowWords is < 1) throw new ArgumentOutOfRangeException(nameof(minimumWindowWords));
        if (minimumDistinctWords < 1 || minimumDistinctWords > minimumWindowWords) throw new ArgumentOutOfRangeException(nameof(minimumDistinctWords));
        if (minimumConfidence is < 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        if (maxContainerWords < minimumWindowWords) throw new ArgumentOutOfRangeException(nameof(maxContainerWords));
        BitTolerance = bitTolerance;
        MinimumWindowWords = minimumWindowWords;
        MinimumDistinctWords = minimumDistinctWords;
        MinimumConfidence = minimumConfidence;
        MaxContainerWords = maxContainerWords;
    }

    public int BitTolerance { get; }
    public int MinimumWindowWords { get; }
    public int MinimumDistinctWords { get; }
    public decimal MinimumConfidence { get; }
    public int MaxContainerWords { get; }

    public static PartialAudioOptions Default { get; } = new();
}
