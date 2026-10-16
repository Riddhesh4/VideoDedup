using System.Text.Json;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>Durable atomic JSON snapshot store until the SQLite repository is implemented in Phase 3.</summary>
public sealed class DirectoryDiscoveryRepository : IDiscoveryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DirectoryDiscoveryRepository(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoDedup", "Discovery");
    }

    public async Task<IReadOnlyList<IndexedFile>> GetFilesAsync(Guid scanRootId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(scanRootId, cancellationToken).ConfigureAwait(false);
            return (await ReadAsync(scanRootId, cancellationToken).ConfigureAwait(false)).Files.ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetVolumeSignatureAsync(Guid scanRootId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(scanRootId, cancellationToken).ConfigureAwait(false);
            return (await ReadAsync(scanRootId, cancellationToken).ConfigureAwait(false)).VolumeSignature;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveFileAsync(IndexedFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(file.ScanRootId, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadAsync(file.ScanRootId, cancellationToken).ConfigureAwait(false);
            var index = snapshot.Files.FindIndex(existing => string.Equals(existing.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) snapshot.Files[index] = file;
            else snapshot.Files.Add(file);
            await WriteAsync(file.ScanRootId, snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetRootStatusAsync(Guid scanRootId, string? volumeSignature, RootAvailability availability, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(scanRootId, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadAsync(scanRootId, cancellationToken).ConfigureAwait(false);
            snapshot.VolumeSignature = volumeSignature ?? snapshot.VolumeSignature;
            snapshot.Availability = availability;
            await WriteAsync(scanRootId, snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<FileStream> AcquireProcessLockAsync(Guid scanRootId, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DiscoveryRootUnavailableException("Discovery index storage is not available.", exception);
        }

        var path = SnapshotPath(scanRootId) + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new DiscoveryRootUnavailableException("Discovery index storage access was denied.", exception);
            }
        }
    }

    private async Task<DiscoverySnapshot> ReadAsync(Guid scanRootId, CancellationToken cancellationToken)
    {
        var path = SnapshotPath(scanRootId);
        if (!File.Exists(path)) return new DiscoverySnapshot();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<DiscoverySnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                   ?? new DiscoverySnapshot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new DiscoveryRootUnavailableException("Discovery index snapshot is not readable; existing records were retained.", exception);
        }
    }

    private async Task WriteAsync(Guid scanRootId, DiscoverySnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DiscoveryRootUnavailableException("Discovery index storage is not available.", exception);
        }

        TryRestrictToCurrentUser();
        var path = SnapshotPath(scanRootId);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            try
            {
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new DiscoveryRootUnavailableException("Discovery index snapshot could not be persisted; existing records were retained.", exception);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void TryRestrictToCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (identity is null) return;
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            new DirectoryInfo(_dataDirectory).SetAccessControl(security);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException
            or IOException or PlatformNotSupportedException or InvalidOperationException)
        {
        }
    }

    private string SnapshotPath(Guid scanRootId) => Path.Combine(_dataDirectory, $"{scanRootId:N}.json");

    private sealed class DiscoverySnapshot
    {
        public string? VolumeSignature { get; set; }
        public RootAvailability Availability { get; set; }
        public List<IndexedFile> Files { get; set; } = new();
    }
}
