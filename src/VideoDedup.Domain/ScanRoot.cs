namespace VideoDedup.Domain;

/// <summary>A configured filesystem root. Network roots may be represented but policy decides when to scan.</summary>
public sealed record ScanRoot
{
    private ScanRoot(Guid id, string label, string path, ScanRootType rootType, DriveDetails? driveDetails, bool enabled, bool includeSubfolders,
        IReadOnlyList<string> includedPatterns, IReadOnlyList<string> excludedPatterns)
    {
        Id = id;
        Label = label;
        Path = path;
        RootType = rootType;
        DriveDetails = driveDetails;
        Enabled = enabled;
        IncludeSubfolders = includeSubfolders;
        IncludedPatterns = Array.AsReadOnly(includedPatterns.ToArray());
        ExcludedPatterns = Array.AsReadOnly(excludedPatterns.ToArray());
    }

    public Guid Id { get; }
    public string Label { get; }
    public string Path { get; }
    public ScanRootType RootType { get; }
    public DriveDetails? DriveDetails { get; }
    public bool Enabled { get; }
    public bool IncludeSubfolders { get; }
    public IReadOnlyList<string> IncludedPatterns { get; }
    public IReadOnlyList<string> ExcludedPatterns { get; }

    public static ScanRoot Create(Guid id, string label, string path, ScanRootType rootType,
        bool enabled = true, bool includeSubfolders = true,
        IEnumerable<string>? includedPatterns = null, IEnumerable<string>? excludedPatterns = null,
        DriveDetails? driveDetails = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Root ID must not be empty.", nameof(id));
        var normalizedLabel = label?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLabel)) throw new ArgumentException("Label must not be blank.", nameof(label));
        var normalizedPath = NormalizeRootPath(path);
        if (!Enum.IsDefined(rootType)) throw new ArgumentOutOfRangeException(nameof(rootType));
        if (rootType == ScanRootType.UsbDrive && driveDetails?.Kind != DriveKind.Usb)
            throw new ArgumentException("USB roots require USB drive details.", nameof(driveDetails));
        if (rootType == ScanRootType.LocalFolder && driveDetails?.Kind == DriveKind.Network)
            throw new ArgumentException("Local-folder roots cannot use network drive details.", nameof(driveDetails));
        if (rootType == ScanRootType.NetworkShare && driveDetails?.Kind != DriveKind.Network)
            throw new ArgumentException("Network-share roots require network drive details.", nameof(driveDetails));

        return new ScanRoot(id, normalizedLabel, normalizedPath, rootType, driveDetails, enabled, includeSubfolders,
            NormalizePatterns(includedPatterns, nameof(includedPatterns)),
            NormalizePatterns(excludedPatterns, nameof(excludedPatterns)));
    }

    private static string NormalizeRootPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Root path must not be blank.", nameof(value));
        var trimmed = value.Trim().Replace('\\', '/');
        if (trimmed.Any(char.IsControl)) throw new ArgumentException("Root path cannot contain control characters.", nameof(value));
        while (trimmed.Length > 1 && trimmed.EndsWith("/", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        return trimmed;
    }

    private static IReadOnlyList<string> NormalizePatterns(IEnumerable<string>? patterns, string parameterName)
    {
        if (patterns is null) return Array.Empty<string>();
        var normalized = patterns.Select(pattern => pattern?.Trim())
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Any(pattern => pattern.Any(char.IsControl)))
            throw new ArgumentException("Patterns cannot contain control characters.", parameterName);
        return normalized;
    }
}
