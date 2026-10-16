namespace VideoDedup.Domain;

/// <summary>Chosen quarantine directory for one source file, with the reason it was chosen.</summary>
public sealed record QuarantineDestination(string Directory, bool SameVolume, string Reason);

/// <summary>
/// Pure destination rule: prefer a same-volume quarantine directory so moving a file stays a metadata operation,
/// but never pick a location inside a configured scan root - the scanner would simply index the quarantined file
/// again. Everything is decided from strings only; the caller performs existence and writability checks.
/// </summary>
public static class QuarantineDestinationPolicy
{
    public const string SameVolumeFolderName = "VideoDedup Quarantine";

    public static QuarantineDestination Choose(string sourcePath, string? sameVolumeBasePath,
        IEnumerable<string> configuredRootPaths, string fallbackDirectory, string fallbackKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackKey);
        ArgumentNullException.ThrowIfNull(configuredRootPaths);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("Quarantine sources must be absolute paths.", nameof(sourcePath));
        }

        if (!Path.IsPathFullyQualified(fallbackDirectory))
        {
            throw new ArgumentException("The fallback quarantine directory must be an absolute path.", nameof(fallbackDirectory));
        }

        var roots = configuredRootPaths.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Normalize).ToArray();
        var key = Normalize(fallbackKey);
        var fallback = Combine(fallbackDirectory, key);

        if (sameVolumeBasePath is not null && Path.IsPathFullyQualified(sameVolumeBasePath))
        {
            var candidate = Combine(sameVolumeBasePath, SameVolumeFolderName);
            var source = Normalize(sourcePath);
            var collidesWithRoot = roots.Any(root => IsWithin(candidate, root));
            var shadowsRoot = roots.Any(root => IsWithin(root, candidate));
            var containsSource = IsWithin(source, candidate);
            if (!collidesWithRoot && !shadowsRoot && !containsSource)
            {
                return new QuarantineDestination(candidate, SameVolume: true,
                    "Quarantine directory sits on the same volume as the source, outside every scan root.");
            }
        }

        return new QuarantineDestination(fallback, SameVolume: false,
            "No safe same-volume quarantine location is available, so the per-user quarantine store is used and the move copies data across volumes.");
    }

    /// <summary>True when <paramref name="candidatePath"/> is the root itself or sits beneath it on a directory boundary.</summary>
    public static bool IsWithin(string candidatePath, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var candidate = Normalize(candidatePath);
        var root = Normalize(rootPath);
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!root.EndsWith('/'))
        {
            root += "/";
        }

        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    private static string Combine(string directory, string leaf) => Normalize(directory) + "/" + leaf;
}
