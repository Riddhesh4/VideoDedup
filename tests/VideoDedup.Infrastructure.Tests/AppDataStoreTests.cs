using FluentAssertions;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class AppDataStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "video-dedup-appdata-" + Guid.NewGuid().ToString("N"));

    public AppDataStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Enumerate_classifies_and_measures_every_file_it_owns()
    {
        Write("videodedup.db", 2048);
        Write(Path.Combine("Discovery", "files.json"), 100);
        Write(Path.Combine("logs", "processing.jsonl"), 50);
        Write(Path.Combine("Quarantine", "copy.mkv"), 4096);
        Write("scan-roots.json", 20);
        Write("notes.txt", 5);

        var entries = new AppDataStore(_root).Enumerate();

        entries.Should().HaveCount(6);
        entries.Single(entry => entry.FullPath.EndsWith("videodedup.db", StringComparison.Ordinal)).Category
            .Should().Be(AppDataCategory.Index);
        entries.Single(entry => entry.FullPath.EndsWith("files.json", StringComparison.Ordinal)).Category
            .Should().Be(AppDataCategory.DiscoveryIndex);
        entries.Single(entry => entry.FullPath.EndsWith("processing.jsonl", StringComparison.Ordinal)).Category
            .Should().Be(AppDataCategory.Logs);
        entries.Single(entry => entry.FullPath.EndsWith("copy.mkv", StringComparison.Ordinal)).Category
            .Should().Be(AppDataCategory.QuarantineStore);
        entries.Single(entry => entry.FullPath.EndsWith("scan-roots.json", StringComparison.Ordinal)).Category
            .Should().Be(AppDataCategory.Configuration);
        entries.Single(entry => entry.FullPath.EndsWith("notes.txt", StringComparison.Ordinal)).Category
            .Should().Be(AppDataCategory.Unknown);
        entries.Single(entry => entry.FullPath.EndsWith("copy.mkv", StringComparison.Ordinal)).SizeBytes.Should().Be(4096);
        entries.Select(entry => entry.FullPath).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Enumerate_of_a_folder_that_does_not_exist_yet_returns_nothing_instead_of_throwing()
    {
        new AppDataStore(Path.Combine(_root, "missing")).Enumerate().Should().BeEmpty();
        FluentActions.Invoking(() => new AppDataStore("relative")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new AppDataStore(" ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeleteFile_refuses_anything_outside_the_application_data_folder()
    {
        var store = new AppDataStore(_root);
        var owned = Write("videodedup.db", 10);
        var outside = Path.Combine(Path.GetTempPath(), "video-dedup-outside-" + Guid.NewGuid().ToString("N"), "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "keep me");
        try
        {
            store.DeleteFile(owned);
            File.Exists(owned).Should().BeFalse();

            store.Invoking(candidate => candidate.DeleteFile(outside)).Should().Throw<InvalidOperationException>();
            File.Exists(outside).Should().BeTrue("source files are never reachable from this store");
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(Path.GetDirectoryName(outside)!);
        }
    }

    [Fact]
    public void DeleteEmptyDirectories_tidies_emptied_folders_and_keeps_the_data_folder_and_used_ones()
    {
        Write(Path.Combine("Quarantine", "copy.mkv"), 10);
        Directory.CreateDirectory(Path.Combine(_root, "logs", "spent"));
        Directory.CreateDirectory(Path.Combine(_root, "Discovery"));

        var store = new AppDataStore(_root);
        store.DeleteEmptyDirectories();

        Directory.Exists(_root).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "logs", "spent")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "Discovery")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "Quarantine")).Should().BeTrue("folders holding data stay");
    }

    [Fact]
    public void WriteText_creates_parent_folders_replaces_the_target_and_leaves_no_temporary_files()
    {
        var store = new AppDataStore(_root);
        var destination = Path.Combine(_root, "..", "vd-export-" + Guid.NewGuid().ToString("N"), "export.json");

        store.WriteText(destination, "{\"first\":1}");
        store.WriteText(destination, "{\"second\":2}");

        try
        {
            File.ReadAllText(destination).Should().Be("{\"second\":2}");
            Directory.GetFiles(Path.GetDirectoryName(destination)!).Should().HaveCount(1);
            store.FileExists(destination).Should().BeTrue();
            store.FileExists(Path.Combine(_root, "nope.json")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(destination)!, recursive: true);
        }
    }
}
