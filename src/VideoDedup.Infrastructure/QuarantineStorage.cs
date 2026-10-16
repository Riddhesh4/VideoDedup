using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>
/// Quarantine filesystem operations with no shell and no overwrite. A same-volume move is a metadata operation;
/// a cross-volume move copies to a deterministic partial file beside the destination, verifies the length, renames
/// it into place, and only then removes the source. If the final source removal fails, both files exist and the
/// durable log's recovery pass reports it instead of pretending the move succeeded.
/// </summary>
public sealed class QuarantineStorage : IQuarantineStorage
{
    private const string PartialSuffix = ".videodedup-partial";

    private const int CopyBufferBytes = 1024 * 1024;

    private readonly Func<string, string?> _volumeRoot;

    /// <summary>
    /// The optional resolver exists so the cross-volume copy path can be exercised on a machine with a single
    /// volume; production leaves it unset and volume roots come from the path itself.
    /// </summary>
    public QuarantineStorage(Func<string, string?>? volumeRootResolver = null) =>
        _volumeRoot = volumeRootResolver ?? DefaultVolumeRoot;

    public StorageInspection Inspect(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                var link = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                return new StorageInspection(true, link, false, info.Length, info.LastWriteTimeUtc);
            }

            if (Directory.Exists(path))
            {
                return new StorageInspection(true, false, true, 0, null);
            }

            return new StorageInspection(false, false, false, 0, null);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new StorageException(StorageFailure.AccessDenied, "Access to the path was denied.", exception);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            var failure = exception is NotSupportedException ? StorageFailure.Unsupported : StorageFailure.Unknown;
            throw new StorageException(failure, "The path could not be inspected.", exception);
        }
    }

    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public bool IsWritableDirectory(string path)
    {
        var probeDirectory = NearestExistingDirectory(path);
        if (probeDirectory is null)
        {
            return false;
        }

        var probe = Path.Combine(probeDirectory, ".videodedup-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public string? GetVolumeRoot(string path) => _volumeRoot(path);

    private static string? DefaultVolumeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    public void EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new StorageException(StorageFailure.AccessDenied, "The directory could not be created.", exception);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            var failure = exception is NotSupportedException ? StorageFailure.Unsupported : StorageFailure.Unknown;
            throw new StorageException(failure, "The directory could not be created.", exception);
        }
    }

    public void Move(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var sourceInspection = Inspect(source);
        if (!sourceInspection.Exists || sourceInspection.IsDirectory)
        {
            throw new StorageException(StorageFailure.NotFound, "The source file does not exist.");
        }

        if (Inspect(destination) is { Exists: true })
        {
            throw new StorageException(StorageFailure.Conflict, "The destination already exists; nothing was overwritten.");
        }

        if (SameVolume(source, destination))
        {
            MoveWithinVolume(source, destination);
            return;
        }

        CopyAcrossVolumes(source, destination);
    }

    private bool SameVolume(string source, string destination) =>
        string.Equals(_volumeRoot(source), _volumeRoot(destination), StringComparison.OrdinalIgnoreCase);

    private static void MoveWithinVolume(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new StorageException(StorageFailure.AccessDenied, "The file could not be moved.", exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new StorageException(StorageFailure.NotFound, "The source file disappeared before the move.", exception);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            throw new StorageException(StorageFailure.Unknown, "The file could not be moved.", exception);
        }
    }

    private void CopyAcrossVolumes(string source, string destination)
    {
        var partial = destination + PartialSuffix;
        try
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            var sourceInfo = new FileInfo(source);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes,
                       FileOptions.WriteThrough))
            {
                input.CopyTo(output, CopyBufferBytes);
                output.Flush(true);
            }

            // The copy must match byte-for-byte in length and keep the original timestamp before it replaces anything.
            if (new FileInfo(partial).Length != sourceInfo.Length)
            {
                throw new StorageException(StorageFailure.Unknown, "The copied file does not match the original length.");
            }

            File.SetLastWriteTimeUtc(partial, sourceInfo.LastWriteTimeUtc);
            File.Move(partial, destination);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            TryDelete(partial);
            var failure = exception switch
            {
                UnauthorizedAccessException => StorageFailure.AccessDenied,
                NotSupportedException => StorageFailure.Unsupported,
                _ => StorageFailure.Unknown,
            };
            throw new StorageException(failure, "The file could not be copied to the quarantine folder.", exception);
        }
        catch (StorageException)
        {
            TryDelete(partial);
            throw;
        }

        try
        {
            File.Delete(source);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // No data loss: the quarantine copy is in place and the log's recovery pass reports both files.
            throw new StorageException(StorageFailure.Unknown,
                "The quarantine copy was written but the original could not be removed; both files now exist.", exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? NearestExistingDirectory(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                current = parent ?? string.Empty;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }

        return null;
    }
}
