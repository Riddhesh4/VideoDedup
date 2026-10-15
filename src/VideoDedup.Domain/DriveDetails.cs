namespace VideoDedup.Domain;

/// <summary>Platform-neutral details identifying a storage volume across rescans.</summary>
public sealed record DriveDetails
{
    private DriveDetails(string volumeSignature, DriveKind kind, string? label, long? capacityBytes)
    {
        VolumeSignature = volumeSignature;
        Kind = kind;
        Label = label;
        CapacityBytes = capacityBytes;
    }

    /// <summary>Stable platform-supplied volume identity; never a drive letter or mount path.</summary>
    public string VolumeSignature { get; }

    public DriveKind Kind { get; }

    public string? Label { get; }

    public long? CapacityBytes { get; }

    public static DriveDetails Create(string volumeSignature, DriveKind kind, string? label = null, long? capacityBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeSignature);
        var signature = volumeSignature.Trim();
        if (signature.Any(char.IsControl))
        {
            throw new ArgumentException("Volume signature cannot contain control characters.", nameof(volumeSignature));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (capacityBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        }

        var normalizedLabel = label?.Trim();
        if (normalizedLabel?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("Drive label cannot contain control characters.", nameof(label));
        }

        return new DriveDetails(signature, kind, string.IsNullOrEmpty(normalizedLabel) ? null : normalizedLabel, capacityBytes);
    }
}
