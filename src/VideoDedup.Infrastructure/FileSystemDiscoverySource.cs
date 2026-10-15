using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

public sealed class FileSystemDiscoverySource : IFileDiscoverySource
{
    public Task<DriveDetails> GetDriveDetailsAsync(ScanRoot root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();

        string driveRoot;
        try
        {
            driveRoot = Path.GetPathRoot(Path.GetFullPath(root.Path)) ?? root.Path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new DiscoveryRootUnavailableException($"Configured root '{root.Label}' is not available.", exception);
        }

        DriveInfo drive;
        bool ready;
        try
        {
            drive = new DriveInfo(driveRoot);
            ready = drive.IsReady;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new DiscoveryRootUnavailableException($"Configured root '{root.Label}' is not available.", exception);
        }

        if (!ready)
            throw new DriveNotFoundException($"Configured root '{root.Label}' is not available.");

        var kind = root.RootType switch
        {
            ScanRootType.UsbDrive => DriveKind.Usb,
            ScanRootType.NetworkShare => DriveKind.Network,
            _ => DriveKind.Local,
        };
        string signature;
        string? label;
        long? capacity;
        try
        {
            signature = root.DriveDetails?.VolumeSignature ?? BuildVolumeSignature(drive);
            label = drive.VolumeLabel;
            capacity = drive.TotalSize;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new DiscoveryRootUnavailableException($"Configured root '{root.Label}' is not available.", exception);
        }

        return Task.FromResult(DriveDetails.Create(signature, kind, label, capacity));
    }

    public async IAsyncEnumerable<FileObservation> EnumerateFilesAsync(
        ScanRoot root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root.Path))
            throw new DiscoveryRootUnavailableException($"Configured root '{root.Label}' is not available.");

        var pending = new Stack<string>();
        pending.Push(root.Path);
        var visited = new HashSet<string>(PathComparer);
        string rootFullPath;
        try
        {
            rootFullPath = Path.GetFullPath(root.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new DiscoveryRootUnavailableException("The configured root path is invalid or unsupported.", exception);
        }
        var pendingCanonical = new HashSet<string>(PathComparer) { rootFullPath };
        const int maxVisitedDirectories = 100_000;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string canonical;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                canonical = Path.GetFullPath(directory);
                if (!visited.Add(canonical)) continue;
                if (visited.Count > maxVisitedDirectories)
                    throw new DiscoveryRootUnavailableException("Directory traversal limit exceeded; stopping this scan safely.");
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !PathComparer.Equals(canonical, rootFullPath))
                    continue;
            }
            catch (DirectoryNotFoundException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (IOException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (UnauthorizedAccessException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (DirectoryNotFoundException exception) { throw new DiscoveryRootUnavailableException("The configured root disappeared during discovery.", exception); }
            catch (IOException exception) { throw new DiscoveryRootUnavailableException("The configured root became unavailable during discovery.", exception); }
            catch (UnauthorizedAccessException exception) { throw new DiscoveryRootUnavailableException("Access to the configured root was denied.", exception); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                throw new DiscoveryRootUnavailableException("The configured root path is invalid or unsupported.", exception);
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (DirectoryNotFoundException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (IOException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (UnauthorizedAccessException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (DirectoryNotFoundException exception) { throw new DiscoveryRootUnavailableException("The configured root disappeared during discovery.", exception); }
            catch (IOException exception) { throw new DiscoveryRootUnavailableException("The configured root became unavailable during discovery.", exception); }
            catch (UnauthorizedAccessException exception) { throw new DiscoveryRootUnavailableException("Access to the configured root was denied.", exception); }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        var childCanonical = Path.GetFullPath(child);
                        if (pendingCanonical.Add(childCanonical)) pending.Push(child);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (DirectoryNotFoundException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (IOException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (UnauthorizedAccessException) when (!PathComparer.Equals(directory, root.Path)) { continue; }
            catch (DirectoryNotFoundException exception) { throw new DiscoveryRootUnavailableException("The configured root disappeared during discovery.", exception); }
            catch (IOException exception) { throw new DiscoveryRootUnavailableException("The configured root became unavailable during discovery.", exception); }
            catch (UnauthorizedAccessException exception) { throw new DiscoveryRootUnavailableException("Access to the configured root was denied.", exception); }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileObservation? observation = null;
                try
                {
                    var attributes = File.GetAttributes(file);
                    var info = new FileInfo(file);
                    observation = new FileObservation(
                        Path.GetRelativePath(root.Path, file).Replace('\\', '/'),
                        info.Length,
                        info.LastWriteTimeUtc,
                        (attributes & FileAttributes.Hidden) != 0,
                        (attributes & FileAttributes.System) != 0,
                        (attributes & FileAttributes.ReparsePoint) != 0);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }

                if (observation is null) continue;
                yield return observation;
                await Task.Yield();
            }
        }
    }

    private static string BuildVolumeSignature(DriveInfo drive)
    {
        if (OperatingSystem.IsWindows())
        {
            var volumePath = new StringBuilder(1024);
            if (GetVolumePathName(drive.RootDirectory.FullName, volumePath, (uint)volumePath.Capacity))
            {
                var volumeName = new StringBuilder(1024);
                var fileSystem = new StringBuilder(256);
                if (GetVolumeInformation(volumePath.ToString(), volumeName, (uint)volumeName.Capacity,
                        out var serial, out _, out _, fileSystem, (uint)fileSystem.Capacity))
                    return $"winvol:{serial:X8}";
            }
        }

        try
        {
            return $"fallback:{drive.DriveType}:{drive.Name.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "fallback:unknown:UNAVAILABLE";
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] StringBuilder volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, [Out] StringBuilder volumeNameBuffer,
        uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength,
        out uint fileSystemFlags, [Out] StringBuilder fileSystemNameBuffer, uint fileSystemNameSize);
}
