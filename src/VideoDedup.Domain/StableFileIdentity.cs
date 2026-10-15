namespace VideoDedup.Domain;

/// <summary>Conservative result when comparing two observations of a file.</summary>
public enum StableIdentityMatch
{
    Different,
    ConfirmedSame,
    NeedsReview,
}

/// <summary>
/// Stable metadata used to correlate a file across rescans without trusting drive letters.
/// This is not a content identity; SHA-256 is required to prove byte-for-byte equality.
/// </summary>
public sealed record StableFileIdentity
{
    private StableFileIdentity(string? volumeSignature, string relativePath, long sizeBytes, DateTimeOffset modifiedUtc)
    {
        VolumeSignature = NormalizeOptional(volumeSignature);
        RelativePath = NormalizeRelativePath(relativePath);
        SizeBytes = sizeBytes;
        ModifiedUtc = modifiedUtc.ToUniversalTime();
    }

    /// <summary>Volume identity/signature, or null when the platform cannot determine it.</summary>
    public string? VolumeSignature { get; }

    /// <summary>Path relative to the configured root, normalized to '/' separators.</summary>
    public string RelativePath { get; }

    public long SizeBytes { get; }

    public DateTimeOffset ModifiedUtc { get; }

    public static StableFileIdentity Create(
        string? volumeSignature,
        string relativePath,
        long sizeBytes,
        DateTimeOffset modifiedUtc)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "File size cannot be negative.");
        }

        return new StableFileIdentity(volumeSignature, relativePath, sizeBytes, modifiedUtc);
    }

    /// <summary>
    /// Compare observations conservatively. A same-path signature change, missing signature,
    /// or likely rename is never silently treated as the same physical file.
    /// </summary>
    public StableIdentityMatch CompareTo(StableFileIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var samePath = string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);
        var sameRevision = SizeBytes == other.SizeBytes && ModifiedUtc == other.ModifiedUtc;
        var bothSignaturesKnown = VolumeSignature is not null && other.VolumeSignature is not null;
        var sameVolume = bothSignaturesKnown && string.Equals(VolumeSignature, other.VolumeSignature, StringComparison.OrdinalIgnoreCase);

        if (samePath && sameRevision && sameVolume)
        {
            return StableIdentityMatch.ConfirmedSame;
        }

        if (samePath && sameRevision && !sameVolume)
        {
            return StableIdentityMatch.NeedsReview;
        }

        if (!samePath && sameRevision && sameVolume)
        {
            return StableIdentityMatch.NeedsReview;
        }

        return StableIdentityMatch.Different;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        if (normalized is not null && normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Volume signature cannot contain control characters.", nameof(value));
        }

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static string NormalizeRelativePath(string value)
    {
        var path = value.Trim().Replace('\\', '/');
        if (path.Any(char.IsControl) || path.Length == 0 || path.StartsWith("/", StringComparison.Ordinal) ||
            (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            throw new ArgumentException("Path must be non-empty and relative to the scan root.", nameof(value));
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("Path must not contain empty, current-directory, or parent-directory segments.", nameof(value));
        }

        return string.Join('/', segments);
    }
}
