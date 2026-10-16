using System.Text.Json;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

public sealed class DirectoryScanRootRepository : IScanRootRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly string _manifestPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DirectoryScanRootRepository(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoDedup");
        _manifestPath = Path.Combine(_directory, "scan-roots.json");
    }

    public async Task<IReadOnlyList<ScanRoot>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return records.Select(ToDomain).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ScanRoot root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var value = ToRecord(root);
            var index = records.FindIndex(item => item.Id == root.Id);
            if (index < 0) records.Add(value);
            else records[index] = value;
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid rootId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (records.RemoveAll(item => item.Id == rootId) > 0)
                await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Scan-root storage is not available.", exception);
        }

        var path = _manifestPath + ".lock";
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
                throw new InvalidOperationException("Scan-root storage access was denied.", exception);
            }
        }
    }

    private async Task<List<ScanRootRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestPath)) return new List<ScanRootRecord>();
        await using var stream = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<List<ScanRootRecord>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? new List<ScanRootRecord>();
    }

    private async Task WriteAsync(List<ScanRootRecord> roots, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Scan-root storage is not available.", exception);
        }

        TryRestrictToCurrentUser();
        var temporary = _manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, roots, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _manifestPath, overwrite: true);
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
            new DirectoryInfo(_directory).SetAccessControl(security);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException
            or IOException or PlatformNotSupportedException or InvalidOperationException)
        {
        }
    }

    private static ScanRoot ToDomain(ScanRootRecord record) => ScanRoot.Create(record.Id, record.Label,
        record.Path, record.RootType, record.Enabled, record.IncludeSubfolders, record.IncludedPatterns,
        record.ExcludedPatterns, record.Drive is null ? null : DriveDetails.Create(record.Drive.VolumeSignature,
            record.Drive.Kind, record.Drive.Label, record.Drive.CapacityBytes));

    private static ScanRootRecord ToRecord(ScanRoot root) => new()
    {
        Id = root.Id,
        Label = root.Label,
        Path = root.Path,
        RootType = root.RootType,
        Enabled = root.Enabled,
        IncludeSubfolders = root.IncludeSubfolders,
        IncludedPatterns = root.IncludedPatterns.ToList(),
        ExcludedPatterns = root.ExcludedPatterns.ToList(),
        Drive = root.DriveDetails is null ? null : new DriveRecord
        {
            VolumeSignature = root.DriveDetails.VolumeSignature,
            Kind = root.DriveDetails.Kind,
            Label = root.DriveDetails.Label,
            CapacityBytes = root.DriveDetails.CapacityBytes,
        },
    };

    private sealed class ScanRootRecord
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public ScanRootType RootType { get; set; }
        public bool Enabled { get; set; }
        public bool IncludeSubfolders { get; set; }
        public List<string> IncludedPatterns { get; set; } = new();
        public List<string> ExcludedPatterns { get; set; } = new();
        public DriveRecord? Drive { get; set; }
    }

    private sealed class DriveRecord
    {
        public string VolumeSignature { get; set; } = string.Empty;
        public DriveKind Kind { get; set; }
        public string? Label { get; set; }
        public long? CapacityBytes { get; set; }
    }
}
