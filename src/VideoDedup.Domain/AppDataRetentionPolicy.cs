namespace VideoDedup.Domain;

/// <summary>Kinds of data the application owns inside its per-user data folder.</summary>
public enum AppDataCategory
{
    /// <summary>SQLite database holding the queue, processing results, fingerprints, matches and decisions.</summary>
    Index,

    /// <summary>Discovery snapshots and indexed file records kept outside the database.</summary>
    DiscoveryIndex,

    /// <summary>Scan-root and application settings files.</summary>
    Configuration,

    /// <summary>Privacy-safe processing logs.</summary>
    Logs,

    /// <summary>Quarantined copies of the user's media together with the undo log.</summary>
    QuarantineStore,

    /// <summary>Anything the application does not recognize. Always retained.</summary>
    Unknown,
}

/// <summary>One observed entry under the application data folder. Nothing here is followed through a link.</summary>
public sealed record AppDataEntry(string FullPath, AppDataCategory Category, long SizeBytes, bool IsReparsePoint, bool IsDirectory = false);

/// <summary>Whether one entry may be deleted, and the reason the user must be told either way.</summary>
public sealed record AppDataDecision(string FullPath, AppDataCategory Category, bool Deletable, string Reason);

/// <summary>
/// Pure retention rules for the application's own data. Source media is never application data, so every rule here
/// narrows deletion to the application data folder and protects the two things a user would not expect to lose:
/// quarantined copies of their media and the scan-root settings that let them find it again.
/// </summary>
public static class AppDataRetentionPolicy
{
    public const string OutsideDataRootReason = "outside the application data folder";
    public const string DataRootItselfReason = "the application data folder itself";
    public const string ReparsePointReason = "a link or reparse point";
    public const string InsideScanRootReason = "inside a folder you asked the application to scan";
    public const string QuarantineRetainedReason = "quarantined copies of your media are kept; restore or remove them in the review tab first";
    public const string ConfigurationRetainedReason = "settings and scan roots are kept; remove roots in the application if you want them gone";
    public const string UnrecognizedReason = "not a recognized application data kind";
    public const string RebuiltReason = "generated again the next time you scan";

    private static readonly string[] IndexFileNames = ["videodedup.db", "videodedup.db-wal", "videodedup.db-shm"];

    private static readonly string[] QuarantineSegments = ["quarantine"];

    private static readonly string[] DiscoverySegments = ["discovery"];

    private static readonly string[] LogSegments = ["logs"];

    /// <summary>Classifies a path relative to the data folder. Pure string work, so it is stable across machines.</summary>
    public static AppDataCategory ClassifyRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A relative path is required.", nameof(relativePath));
        }

        if (relativePath.Any(char.IsControl))
        {
            throw new ArgumentException("A relative path cannot contain control characters.", nameof(relativePath));
        }

        var normalized = relativePath.Trim().Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return AppDataCategory.Unknown;
        }

        var first = segments[0];
        if (segments.Length > 1)
        {
            if (QuarantineSegments.Contains(first, StringComparer.OrdinalIgnoreCase)) return AppDataCategory.QuarantineStore;
            if (DiscoverySegments.Contains(first, StringComparer.OrdinalIgnoreCase)) return AppDataCategory.DiscoveryIndex;
            if (LogSegments.Contains(first, StringComparer.OrdinalIgnoreCase)) return AppDataCategory.Logs;
            return AppDataCategory.Unknown;
        }

        if (IndexFileNames.Contains(first, StringComparer.OrdinalIgnoreCase)) return AppDataCategory.Index;
        if (first.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return AppDataCategory.Configuration;
        return AppDataCategory.Unknown;
    }

    /// <summary>Decides one entry. <paramref name="scanRoots"/> are the folders the user asked the application to scan.</summary>
    public static AppDataDecision Classify(string dataRoot, AppDataEntry entry, IEnumerable<string>? scanRoots = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The application data folder must be an absolute path.", nameof(dataRoot));
        }

        if (!Path.IsPathFullyQualified(entry.FullPath))
        {
            throw new ArgumentException("An application data entry must be an absolute path.", nameof(entry));
        }

        if (!Enum.IsDefined(entry.Category))
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Unknown application data category.");
        }

        if (entry.SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Size cannot be negative.");
        }

        if (!IsInside(dataRoot, entry.FullPath))
        {
            return Resolve(entry, false, entry.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? DataRootItselfReason
                : OutsideDataRootReason);
        }

        if (entry.IsReparsePoint)
        {
            return Resolve(entry, false, ReparsePointReason);
        }

        foreach (var scanRoot in scanRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(scanRoot) || !Path.IsPathFullyQualified(scanRoot))
            {
                continue;
            }

            if (IsInside(scanRoot, entry.FullPath))
            {
                return Resolve(entry, false, InsideScanRootReason);
            }
        }

        return entry.Category switch
        {
            AppDataCategory.Index or AppDataCategory.DiscoveryIndex or AppDataCategory.Logs => Resolve(entry, true, RebuiltReason),
            AppDataCategory.QuarantineStore => Resolve(entry, false, QuarantineRetainedReason),
            AppDataCategory.Configuration => Resolve(entry, false, ConfigurationRetainedReason),
            _ => Resolve(entry, false, UnrecognizedReason),
        };
    }

    public static IReadOnlyList<AppDataDecision> ClassifyAll(string dataRoot, IEnumerable<AppDataEntry> entries, IEnumerable<string>? scanRoots = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var roots = scanRoots?.ToArray() ?? [];
        return entries.Select(entry => Classify(dataRoot, entry, roots)).ToArray();
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is strictly below <paramref name="parent"/>. Both paths must be absolute;
    /// a sibling with a shared name prefix (C:\Data vs C:\Database) is not inside.
    /// </summary>
    public static bool IsInside(string parent, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        if (!Path.IsPathFullyQualified(parent))
        {
            throw new ArgumentException("The containing path must be absolute.", nameof(parent));
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new ArgumentException("The candidate path must be absolute.", nameof(candidate));
        }

        var normalizedParent = Trim(Path.GetFullPath(parent));
        var normalizedCandidate = Trim(Path.GetFullPath(candidate));
        if (normalizedCandidate.Length <= normalizedParent.Length)
        {
            return false;
        }

        if (!normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundary = normalizedCandidate[normalizedParent.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static AppDataDecision Resolve(AppDataEntry entry, bool deletable, string reason) =>
        new(entry.FullPath, entry.Category, deletable, reason);
}
