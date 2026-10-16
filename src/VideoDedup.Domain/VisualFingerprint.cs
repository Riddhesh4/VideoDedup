namespace VideoDedup.Domain;

/// <summary>Perceptual hash and quality for one sampled video frame.</summary>
public sealed record VisualFingerprint
{
    private VisualFingerprint(TimeSpan timestamp, ulong perceptualHash, decimal quality, FingerprintVersion version, FrameDisposition disposition)
    {
        Timestamp = timestamp;
        PerceptualHash = perceptualHash;
        Quality = quality;
        Version = version;
        Disposition = disposition;
    }

    public TimeSpan Timestamp { get; }

    public ulong PerceptualHash { get; }

    /// <summary>Normalized frame quality in [0, 1].</summary>
    public decimal Quality { get; }

    public FingerprintVersion Version { get; }

    public FrameDisposition Disposition { get; }

    public static VisualFingerprint Create(TimeSpan timestamp, ulong perceptualHash, decimal quality, FingerprintVersion version,
        FrameDisposition disposition = FrameDisposition.Usable)
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

        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        return new VisualFingerprint(timestamp, perceptualHash, quality, version, disposition);
    }
}
