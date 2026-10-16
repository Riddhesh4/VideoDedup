namespace VideoDedup.Domain;

public static class AudioAlgorithm
{
    public const string Name = "chromaprint-fpcalc";
    // v2.0 re-pins stored payloads to fpcalc's raw unsigned 32-bit word list (-json -raw) as
    // little-endian bytes, because Phase 7 similarity needs true word bit patterns; v1.0 stored
    // decoded bytes of chromaprint's compressed encoding, which cannot be Hamming-compared.
    public static FingerprintVersion Version { get; } = FingerprintVersion.Create(2, 0);
}

/// <summary>Chromaprint-like fingerprint for a bounded audio segment.</summary>
public sealed record AudioFingerprint
{
    private AudioFingerprint(TimeSpan start, TimeSpan end, byte[] data, FingerprintVersion version)
    {
        Start = start;
        End = end;
        _data = (byte[])data.Clone();
        Version = version;
    }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    /// <summary>A defensive copy of the fingerprint payload.</summary>
    public byte[] Data => (byte[])_data.Clone();

    public FingerprintVersion Version { get; }

    private readonly byte[] _data;

    public static AudioFingerprint Create(TimeSpan start, TimeSpan end, ReadOnlySpan<byte> data, FingerprintVersion version)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end <= start)
        {
            throw new ArgumentException("Segment end must be later than segment start.", nameof(end));
        }

        if (data.IsEmpty)
        {
            throw new ArgumentException("Fingerprint data must not be empty.", nameof(data));
        }

        if (version.Major <= 0 || version.Minor < 0)
        {
            throw new ArgumentException("Fingerprint version must be a valid positive version.", nameof(version));
        }

        return new AudioFingerprint(start, end, data.ToArray(), version);
    }
}
