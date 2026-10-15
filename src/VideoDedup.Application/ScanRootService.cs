using VideoDedup.Domain;

namespace VideoDedup.Application;

public interface IScanRootRepository
{
    Task<IReadOnlyList<ScanRoot>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(ScanRoot root, CancellationToken cancellationToken);

    Task DeleteAsync(Guid rootId, CancellationToken cancellationToken);
}

public sealed class ScanRootService(IScanRootRepository roots, IScanOrchestrator scans)
{
    public async Task<IReadOnlyList<ScanRoot>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);

    public async Task AddAsync(ScanRoot root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var configured = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (configured.Any(existing => existing.Id != root.Id && PathsEqual(existing.Path, root.Path)))
            throw new InvalidOperationException("A scan root for this path is already configured.");
        await roots.SaveAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(ScanRoot root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var configured = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (!configured.Any(existing => existing.Id == root.Id))
            throw new KeyNotFoundException($"Scan root '{root.Id}' was not found.");
        if (configured.Any(existing => existing.Id != root.Id && PathsEqual(existing.Path, root.Path)))
            throw new InvalidOperationException("A scan root for this path is already configured.");
        await roots.SaveAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(Guid rootId, bool enabled, CancellationToken cancellationToken = default)
    {
        var configured = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var current = configured.SingleOrDefault(root => root.Id == rootId)
                      ?? throw new KeyNotFoundException($"Scan root '{rootId}' was not found.");
        var updated = ScanRoot.Create(current.Id, current.Label, current.Path, current.RootType, enabled,
            current.IncludeSubfolders, current.IncludedPatterns, current.ExcludedPatterns, current.DriveDetails);
        await roots.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAsync(Guid rootId, CancellationToken cancellationToken = default) =>
        roots.DeleteAsync(rootId, cancellationToken);

    public async Task<DiscoveryResult> RescanAsync(Guid rootId, CancellationToken cancellationToken = default)
    {
        var configured = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var root = configured.SingleOrDefault(item => item.Id == rootId)
                   ?? throw new KeyNotFoundException($"Scan root '{rootId}' was not found.");
        return await scans.ScanAsync(root, cancellationToken).ConfigureAwait(false);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            var unc = "//" + string.Join('/', normalized[2..].Split('/', StringSplitOptions.RemoveEmptyEntries));
            return unc.Length > 2 ? unc.TrimEnd('/') : unc;
        }

        if (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
            return normalized.Length == 3 ? normalized.ToUpperInvariant() : normalized.TrimEnd('/');

        var fullPath = Path.GetFullPath(normalized);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
