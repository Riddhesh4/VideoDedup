namespace VideoDedup.Domain;

/// <summary>Version of the fingerprint algorithm and its normalization contract.</summary>
public readonly record struct FingerprintVersion
{
    private FingerprintVersion(int major, int minor)
    {
        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    public static FingerprintVersion Create(int major, int minor)
    {
        if (major <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), major, "Major version must be positive.");
        }

        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minor), minor, "Minor version cannot be negative.");
        }

        return new FingerprintVersion(major, minor);
    }
}
