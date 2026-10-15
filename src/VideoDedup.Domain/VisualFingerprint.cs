namespace VideoDedup.Domain;

/// <summary>Perceptual hash and quality for one sampled video frame.</summary>
public sealed record VisualFingerprint
{
    private VisualFingerprint(TimeSpan timestamp, ulong perceptualHash, decimal quality, FingerprintVersion version)
    {
        Timestamp = timestamp;
        PerceptualHash = perceptualHash;
        Quality = quality;
        Version = version;
    }

    public TimeSpan Timestamp { get; }

    public ulong PerceptualHash { get; }

    /// <summary>Normalized frame quality in [0, 1].</summary>
    public decimal Quality { get; }

    public FingerprintVersion Version { get; }

    public static VisualFingerprint Create(TimeSpan timestamp, ulong perceptualHash, decimal quality, FingerprintVersion version)
    {
        if (timestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        if (quality is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }

        if (version.Major <= 0 || version.Minor < 0)
        {
            throw new ArgumentException("Fingerprint version must be a valid positive version.", nameof(version));
        }

        return new VisualFingerprint(timestamp, perceptualHash, quality, version);
    }
}
