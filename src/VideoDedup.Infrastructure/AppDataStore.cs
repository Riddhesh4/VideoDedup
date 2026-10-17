using System.Security;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

/// <summary>
/// Filesystem view of the application's own data folder. The walk never descends through a link, every delete is
/// re-checked against <see cref="AppDataRetentionPolicy"/>, and nothing outside the data folder is reachable.
/// </summary>
public sealed class AppDataStore : IAppDataStore
{
    private const int MaxDepth = 32;

    private readonly string _root;

    public AppDataStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The application data folder must be an absolute path.", nameof(dataRoot));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
    }

    public string DataRoot => _root;

    public IReadOnlyList<AppDataEntry> Enumerate()
    {
        var entries = new List<AppDataEntry>();
        if (!Directory.Exists(_root))
        {
            return entries;
        }

        Walk(_root, entries, 0);
        entries.Sort((left, right) => string.CompareOrdinal(left.FullPath, right.FullPath));
        return entries;
    }

    public void DeleteFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsInsideDataRoot(path))
        {
            throw new InvalidOperationException("Only files inside the application data folder can be deleted.");
        }

        File.Delete(path);
    }

    public void DeleteEmptyDirectories()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!IsInsideDataRoot(directory))
            {
                continue;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An undeletable empty folder is not a failure worth surfacing; the index itself is already gone.
            }
        }
    }

    public void WriteText(string destinationPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = Path.Combine(directory ?? string.Empty, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false));
            File.Move(temporary, Path.GetFullPath(destinationPath), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private void Walk(string directory, List<AppDataEntry> entries, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        List<FileSystemInfo> children;
        try
        {
            children = new DirectoryInfo(directory)
                .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None })
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return;
        }

        foreach (var child in children)
        {
            var isReparsePoint = (child.Attributes & FileAttributes.ReparsePoint) != 0;
            if (child is DirectoryInfo nested)
            {
                // A linked folder is never followed: its contents are not application data we understand.
                if (!isReparsePoint)
                {
                    Walk(nested.FullName, entries, depth + 1);
                }

                continue;
            }

            if (child is FileInfo file)
            {
                entries.Add(new AppDataEntry(file.FullName,
                    AppDataRetentionPolicy.ClassifyRelativePath(Path.GetRelativePath(_root, file.FullName)),
                    Length(file), isReparsePoint));
            }
        }
    }

    private static long Length(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private bool IsInsideDataRoot(string path) =>
        Path.IsPathFullyQualified(path) && AppDataRetentionPolicy.IsInside(_root, path);
}
