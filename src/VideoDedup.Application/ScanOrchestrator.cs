using VideoDedup.Domain;

namespace VideoDedup.Application;

public sealed class ScanOrchestrator(IFileDiscoverySource source, IDiscoveryRepository repository) : IScanOrchestrator
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".ts", ".webm", ".wmv",
    };

    public async Task<DiscoveryResult> ScanAsync(ScanRoot root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();

        if (!root.Enabled)
            return Empty(root.Id, DiscoveryOutcome.Disabled);
        if (root.RootType == ScanRootType.NetworkShare)
            return Empty(root.Id, DiscoveryOutcome.NetworkShareNotScanned);

        int enumerated = 0, added = 0, changed = 0, unchanged = 0, review = 0, skipped = 0;
        var warnings = new List<string>();
        IReadOnlyList<IndexedFile> files;
        try
        {
            files = await repository.GetFilesAsync(root.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRootUnavailable(exception))
        {
            await repository.SetRootStatusAsync(root.Id, null, RootAvailability.Unavailable, cancellationToken).ConfigureAwait(false);
            warnings.Add($"Root unavailable ({exception.GetType().Name}); existing file records were retained.");
            return new DiscoveryResult(root.Id, DiscoveryOutcome.RootUnavailable, 0, 0, 0, 0, 0, 0,
                Array.AsReadOnly(warnings.ToArray()));
        }
        var byPath = files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);

        try
        {
            var drive = await source.GetDriveDetailsAsync(root, cancellationToken).ConfigureAwait(false);
            if (drive.Kind != ExpectedDriveKind(root.RootType))
                throw new InvalidOperationException("Discovery source returned a drive kind inconsistent with the configured root.");

            var oldVolume = await repository.GetVolumeSignatureAsync(root.Id, cancellationToken).ConfigureAwait(false);
            var changedVolume = oldVolume is not null && !string.Equals(oldVolume, drive.VolumeSignature, StringComparison.OrdinalIgnoreCase);
            if (changedVolume && files.Count > 0)
                warnings.Add("Volume signature changed. Existing files on this root require review before correlating observations.");

            await foreach (var observed in source.EnumerateFilesAsync(root, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                enumerated++;
                if (!IsEligible(root, observed))
                {
                    skipped++;
                    continue;
                }

                var identity = StableFileIdentity.Create(drive.VolumeSignature, observed.RelativePath,
                    observed.SizeBytes, observed.ModifiedUtc);
                if (byPath.TryGetValue(identity.RelativePath, out var existing))
                {
                    if (changedVolume)
                    {
                        review++;
                        warnings.Add($"File identity is uncertain and needs review: {identity.RelativePath}");
                        continue;
                    }

                    var previous = StableFileIdentity.Create(oldVolume, existing.RelativePath,
                        existing.SizeBytes, existing.ModifiedUtc);
                    var comparison = previous.CompareTo(identity);
                    switch (comparison)
                    {
                        case StableIdentityMatch.ConfirmedSame:
                            unchanged++;
                            continue;
                        case StableIdentityMatch.NeedsReview:
                            review++;
                            warnings.Add($"File identity is uncertain and needs review: {identity.RelativePath}");
                            continue;
                        case StableIdentityMatch.Different:
                            var updated = VideoFile.Create(existing.Id, root.Id, identity.RelativePath,
                                identity.SizeBytes, identity.ModifiedUtc);
                            await repository.SaveFileAsync(ToIndexedFile(updated), cancellationToken).ConfigureAwait(false);
                            changed++;
                            byPath[identity.RelativePath] = ToIndexedFile(updated);
                            continue;
                        default:
                            throw new InvalidOperationException("Unknown stable-identity result.");
                    }
                }

                var possibleRename = files.FirstOrDefault(file =>
                {
                    var priorIdentity = StableFileIdentity.Create(oldVolume, file.RelativePath, file.SizeBytes, file.ModifiedUtc);
                    var sameRevision = priorIdentity.SizeBytes == identity.SizeBytes && priorIdentity.ModifiedUtc == identity.ModifiedUtc;
                    var movedOrChangedVolume = changedVolume && sameRevision;
                    var likelyRename = priorIdentity.CompareTo(identity) == StableIdentityMatch.NeedsReview && sameRevision &&
                                       !string.Equals(priorIdentity.RelativePath, identity.RelativePath, StringComparison.OrdinalIgnoreCase);
                    return movedOrChangedVolume || likelyRename;
                });
                if (possibleRename is not null)
                {
                    review++;
                    warnings.Add($"Possible rename or move needs review: {possibleRename.RelativePath} -> {identity.RelativePath}");
                    continue;
                }

                var discovered = VideoFile.Create(Guid.NewGuid(), root.Id, identity.RelativePath,
                    identity.SizeBytes, identity.ModifiedUtc);
                await repository.SaveFileAsync(ToIndexedFile(discovered), cancellationToken).ConfigureAwait(false);
                byPath.Add(identity.RelativePath, ToIndexedFile(discovered));
                added++;
            }
            await repository.SetRootStatusAsync(root.Id, drive.VolumeSignature, RootAvailability.Available, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRootUnavailable(exception))
        {
            await repository.SetRootStatusAsync(root.Id, null, RootAvailability.Unavailable, cancellationToken).ConfigureAwait(false);
            warnings.Add($"Root unavailable ({exception.GetType().Name}); existing file records were retained.");
            return new DiscoveryResult(root.Id, DiscoveryOutcome.RootUnavailable, enumerated, added, changed,
                unchanged, review, skipped, Array.AsReadOnly(warnings.ToArray()));
        }

        return new DiscoveryResult(root.Id, DiscoveryOutcome.Completed, enumerated, added, changed,
            unchanged, review, skipped, Array.AsReadOnly(warnings.ToArray()));
    }

    private static DriveKind ExpectedDriveKind(ScanRootType type) => type switch
    {
        ScanRootType.LocalFolder => DriveKind.Local,
        ScanRootType.UsbDrive => DriveKind.Usb,
        ScanRootType.NetworkShare => DriveKind.Network,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static IndexedFile ToIndexedFile(VideoFile file) => new(file.Id, file.ScanRootId, file.RelativePath,
        file.SizeBytes, file.ModifiedUtc, file.ScanState, file.Duration, file.Width, file.Height,
        file.VideoCodec, file.AudioCodec, file.HasAudio, file.Sha256);

    private static DiscoveryResult Empty(Guid rootId, DiscoveryOutcome outcome) =>
        new(rootId, outcome, 0, 0, 0, 0, 0, 0, Array.Empty<string>());

    private static bool IsEligible(ScanRoot root, FileObservation entry)
    {
        if (entry.IsHidden || entry.IsSystem || entry.IsSymbolicLink || entry.SizeBytes < 0)
            return false;

        var relative = entry.RelativePath.Replace('\\', '/');
        if (relative is "." or ".." || relative.Length == 0 || relative.StartsWith("/", StringComparison.Ordinal) ||
            (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':') ||
            relative.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            return false;
        if (!SupportedExtensions.Contains(Path.GetExtension(relative)))
            return false;
        if (!root.IncludeSubfolders && relative.Contains('/', StringComparison.Ordinal))
            return false;
        if (root.IncludedPatterns.Count > 0 && !root.IncludedPatterns.Any(pattern => PatternMatches(relative, pattern)))
            return false;
        return !root.ExcludedPatterns.Any(pattern => PatternMatches(relative, pattern));
    }

    private static bool PatternMatches(string path, string pattern)
    {
        var fileName = Path.GetFileName(path);
        var normalizedPattern = pattern.Replace('\\', '/');
        if (normalizedPattern.StartsWith("*.", StringComparison.Ordinal))
            return string.Equals(Path.GetExtension(path), normalizedPattern[1..], StringComparison.OrdinalIgnoreCase);
        if (normalizedPattern.EndsWith("/**", StringComparison.Ordinal))
            return path.StartsWith(normalizedPattern[..^3] + "/", StringComparison.OrdinalIgnoreCase);
        if (normalizedPattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var directory = normalizedPattern[..^2].TrimEnd('/');
            return path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase) &&
                   !path[(directory.Length + 1)..].Contains('/', StringComparison.Ordinal);
        }
        return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootUnavailable(Exception exception) => exception is
        DiscoveryRootUnavailableException or DriveNotFoundException or DirectoryNotFoundException
        or FileNotFoundException or IOException or UnauthorizedAccessException;
}
