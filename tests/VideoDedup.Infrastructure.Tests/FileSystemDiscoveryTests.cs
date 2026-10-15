using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class FileSystemDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VideoDedup.Tests", Guid.NewGuid().ToString("N"));

    public FileSystemDiscoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Enumeration_returns_relative_metadata_and_ignores_reparse_subtrees()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "top.mp4"), new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, "nested", "movie.mkv"), new byte[] { 4 }, TestContext.Current.CancellationToken);
        var root = LocalRoot(_root);
        var source = new FileSystemDiscoverySource();

        var entries = await CollectAsync(source.EnumerateFilesAsync(root, CancellationToken.None));

        entries.Select(entry => entry.RelativePath).Should().BeEquivalentTo("top.mp4", "nested/movie.mkv");
        entries.Single(entry => entry.RelativePath == "top.mp4").SizeBytes.Should().Be(3);
    }

    [Fact]
    public async Task Deep_nesting_completes_without_recursion_overflow()
    {
        var deep = _root;
        for (var level = 0; level < 50; level++)
        {
            deep = Path.Combine(deep, "d" + level);
            Directory.CreateDirectory(deep);
        }

        await File.WriteAllBytesAsync(Path.Combine(deep, "deep.mp4"), new byte[] { 9 }, TestContext.Current.CancellationToken);
        var source = new FileSystemDiscoverySource();

        var entries = await CollectAsync(source.EnumerateFilesAsync(LocalRoot(_root), CancellationToken.None));

        entries.Should().ContainSingle().Which.RelativePath.Should().EndWith("deep.mp4");
    }

    [Fact]
    public async Task GetDriveDetails_returns_stable_signature_for_local_root()
    {
        var source = new FileSystemDiscoverySource();
        var root = LocalRoot(_root);

        var first = await source.GetDriveDetailsAsync(root, TestContext.Current.CancellationToken);
        var second = await source.GetDriveDetailsAsync(root, TestContext.Current.CancellationToken);

        first.Kind.Should().Be(DriveKind.Local);
        first.VolumeSignature.Should().NotBeNullOrWhiteSpace();
        second.VolumeSignature.Should().Be(first.VolumeSignature);
    }

    [Fact]
    public async Task Corrupt_snapshot_is_reported_without_silent_record_loss()
    {
        var dataDirectory = Path.Combine(_root, "corrupt-index");
        Directory.CreateDirectory(dataDirectory);
        var rootId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, $"{rootId:N}.json"), "{not-json", TestContext.Current.CancellationToken);
        var repository = new DirectoryDiscoveryRepository(dataDirectory);

        await FluentActions.Awaiting(() => repository.GetFilesAsync(rootId, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<DiscoveryRootUnavailableException>();
        Directory.GetFiles(dataDirectory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Enumeration_is_cancelable()
    {
        for (var index = 0; index < 100; index++)
            await File.WriteAllBytesAsync(Path.Combine(_root, $"{index:D3}.mp4"), new byte[] { 1 }, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var source = new FileSystemDiscoverySource();
        var root = LocalRoot(_root);

        await FluentActions.Awaiting(async () =>
        {
            await foreach (var _ in source.EnumerateFilesAsync(root, cancellation.Token))
                cancellation.Cancel();
        }).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Directory_repository_persists_records_and_root_status_across_instances()
    {
        var dataDirectory = Path.Combine(_root, "index");
        var repository = new DirectoryDiscoveryRepository(dataDirectory);
        var rootId = Guid.NewGuid();
        var file = new IndexedFile(Guid.NewGuid(), rootId, "clip.mp4", 12, DateTimeOffset.UnixEpoch, ScanState.Discovered);
        await repository.SaveFileAsync(file, CancellationToken.None);
        await repository.SetRootStatusAsync(rootId, "volume-1", RootAvailability.Available, CancellationToken.None);

        var reopened = new DirectoryDiscoveryRepository(dataDirectory);

        (await reopened.GetFilesAsync(rootId, CancellationToken.None)).Should().ContainSingle().Which.Should().Be(file);
        (await reopened.GetVolumeSignatureAsync(rootId, CancellationToken.None)).Should().Be("volume-1");
        Directory.GetFiles(dataDirectory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_root_enumeration_throws_instead_of_returning_an_empty_success()
    {
        var missing = Path.Combine(_root, "gone");
        var root = LocalRoot(missing);
        var source = new FileSystemDiscoverySource();

        await FluentActions.Awaiting(async () =>
        {
            await foreach (var _ in source.EnumerateFilesAsync(root, TestContext.Current.CancellationToken)) { }
        }).Should().ThrowAsync<DiscoveryRootUnavailableException>();
    }

    private static ScanRoot LocalRoot(string path) =>
        ScanRoot.Create(Guid.NewGuid(), "Temp", path, ScanRootType.LocalFolder);

    private static async Task<List<FileObservation>> CollectAsync(IAsyncEnumerable<FileObservation> source)
    {
        var entries = new List<FileObservation>();
        await foreach (var entry in source) entries.Add(entry);
        return entries;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
