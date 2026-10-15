using System.Runtime.CompilerServices;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Application.Tests;

public sealed class DiscoveryOrchestratorTests
{
    [Fact]
    public async Task First_scan_adds_supported_files_and_skips_hidden_unsupported_and_linked_files()
    {
        var context = Create();
        context.Source.Items = [File("clip.mp4"), File("photo.jpg"), File("hidden.mkv", hidden: true),
            File("linked.mov", link: true), File("sub/valid.webm")];

        var result = await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(DiscoveryOutcome.Completed);
        result.AddedCount.Should().Be(2);
        result.SkippedCount.Should().Be(3);
        context.Repository.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task Rescan_skips_unchanged_files_and_does_not_create_duplicate_records()
    {
        var context = Create();
        context.Source.Items = [File("folder/clip.mp4")];

        var first = await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);
        var second = await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);

        first.AddedCount.Should().Be(1);
        second.AddedCount.Should().Be(0);
        second.UnchangedCount.Should().Be(1);
        context.Repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Changed_volume_signature_with_same_path_and_revision_is_reviewable()
    {
        var context = Create();
        context.Source.Items = [File("clip.mp4")];
        await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);
        context.Source.Drive = DriveDetails.Create("volume-B", DriveKind.Local);

        var result = await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);

        result.NeedsReviewCount.Should().Be(1);
        result.UnchangedCount.Should().Be(0);
        result.ChangedCount.Should().Be(0);
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain(w => w.Contains("Volume signature changed"));
    }

    [Fact]
    public async Task Changed_volume_with_changed_revision_is_reviewable_not_silently_updated()
    {
        var context = Create();
        context.Source.Items = [File("clip.mp4")];
        await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);
        context.Source.Drive = DriveDetails.Create("volume-B", DriveKind.Local);
        context.Source.Items = [File("clip.mp4", size: 200)];

        var result = await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);

        result.NeedsReviewCount.Should().Be(1);
        result.ChangedCount.Should().Be(0);
        result.AddedCount.Should().Be(0);
        context.Repository.Files.Should().ContainSingle().Which.SizeBytes.Should().Be(100);
        result.Warnings.Should().Contain(w => w.Contains("Volume signature changed"));
    }

    [Fact]
    public async Task Same_path_with_changed_revision_updates_existing_index_record()
    {
        var context = Create();
        context.Source.Items = [File("clip.mp4")];
        await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);
        context.Source.Items = [File("clip.mp4", size: 200)];

        var result = await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);

        result.ChangedCount.Should().Be(1);
        result.AddedCount.Should().Be(0);
        context.Repository.Files.Should().ContainSingle().Which.SizeBytes.Should().Be(200);
    }

    [Fact]
    public async Task Possible_rename_is_not_silently_added_as_a_second_file()
    {
        var context = Create();
        context.Source.Items = [File("old-name.mp4")];
        await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);
        context.Source.Items = [File("new-name.mp4")];

        var result = await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);

        result.NeedsReviewCount.Should().Be(1);
        context.Repository.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task Offline_roots_keep_records_and_disabled_or_network_roots_are_not_scanned()
    {
        var context = Create();
        context.Source.Items = [File("clip.mp4")];
        await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);
        context.Source.ThrowOnDriveLookup = true;

        var offline = await context.Orchestrator.ScanAsync(context.Root, TestContext.Current.CancellationToken);
        offline.Outcome.Should().Be(DiscoveryOutcome.RootUnavailable);
        context.Repository.Files.Should().ContainSingle();

        var disabled = ScanRoot.Create(Guid.NewGuid(), "Off", "C:/Off", ScanRootType.LocalFolder, enabled: false);
        (await context.Orchestrator.ScanAsync(disabled, TestContext.Current.CancellationToken)).Outcome.Should().Be(DiscoveryOutcome.Disabled);
        var network = ScanRoot.Create(Guid.NewGuid(), "NAS", "//server/share", ScanRootType.NetworkShare,
            driveDetails: DriveDetails.Create("network", DriveKind.Network));
        (await context.Orchestrator.ScanAsync(network, TestContext.Current.CancellationToken)).Outcome.Should().Be(DiscoveryOutcome.NetworkShareNotScanned);
    }

    [Fact]
    public async Task Cancellation_is_propagated_not_returned_as_unavailable()
    {
        var context = Create();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Source.CancelOnEnumeration = cancellation.Cancel;

        await FluentActions.Awaiting(() => context.Orchestrator.ScanAsync(context.Root, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        context.Repository.LastAvailability.Should().BeNull();
    }

    private static TestContextData Create()
    {
        var root = ScanRoot.Create(Guid.NewGuid(), "Movies", "C:/Movies", ScanRootType.LocalFolder);
        var source = new FakeDiscoverySource();
        var repository = new FakeDiscoveryRepository();
        return new TestContextData(root, source, repository, new ScanOrchestrator(source, repository));
    }

    private static FileObservation File(string path, long size = 100, bool hidden = false, bool system = false, bool link = false) =>
        new(path, size, DateTimeOffset.Parse("2025-01-02T03:04:05Z"), hidden, system, link);

    private sealed record TestContextData(ScanRoot Root, FakeDiscoverySource Source, FakeDiscoveryRepository Repository, ScanOrchestrator Orchestrator);

    private sealed class FakeDiscoverySource : IFileDiscoverySource
    {
        public DriveDetails Drive { get; set; } = DriveDetails.Create("volume-A", DriveKind.Local);
        public IReadOnlyList<FileObservation> Items { get; set; } = Array.Empty<FileObservation>();
        public bool ThrowOnDriveLookup { get; set; }
        public CancellationTokenSource? CancelAfterFirstFile { get; set; }
        public Action? CancelOnEnumeration { get; set; }

        public Task<DriveDetails> GetDriveDetailsAsync(ScanRoot root, CancellationToken cancellationToken) =>
            ThrowOnDriveLookup ? Task.FromException<DriveDetails>(new DirectoryNotFoundException()) : Task.FromResult(Drive);

        public async IAsyncEnumerable<FileObservation> EnumerateFilesAsync(ScanRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CancelOnEnumeration?.Invoke();
            foreach (var item in Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                if (CancelAfterFirstFile is not null)
                {
                    CancelAfterFirstFile.Cancel();
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Task.Yield();
            }
        }
    }

    private sealed class FakeDiscoveryRepository : IDiscoveryRepository
    {
        private readonly Dictionary<string, IndexedFile> _files = new(StringComparer.OrdinalIgnoreCase);
        public int SaveCount { get; private set; }
        public RootAvailability? LastAvailability { get; private set; }

        public Task<IReadOnlyList<IndexedFile>> GetFilesAsync(Guid scanRootId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IndexedFile>>(_files.Values.ToArray());
        }

        public Task<string?> GetVolumeSignatureAsync(Guid scanRootId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(_volumeSignature);
        }

        private string? _volumeSignature;

        public Task SaveFileAsync(IndexedFile file, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files[file.RelativePath] = file;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task SetRootStatusAsync(Guid scanRootId, string? volumeSignature, RootAvailability availability, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAvailability = availability;
            if (volumeSignature is not null) _volumeSignature = volumeSignature;
            return Task.CompletedTask;
        }

        public IReadOnlyCollection<IndexedFile> Files => _files.Values;
    }
}
