using System.Runtime.CompilerServices;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Application.Tests;

public sealed class ScanRootServiceTests
{
    [Fact]
    public async Task Add_rejects_duplicate_paths_but_allows_distinct_roots()
    {
        var repository = new FakeRoots();
        var service = new ScanRootService(repository, new FakeScans());
        var first = Root("C:/Movies");
        await service.AddAsync(first, TestContext.Current.CancellationToken);

        await FluentActions.Awaiting(() => service.AddAsync(Root("c:/movies/"), TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>();
        await service.AddAsync(Root("D:/Movies"), TestContext.Current.CancellationToken);

        (await service.GetAllAsync(TestContext.Current.CancellationToken)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Enable_disable_update_remove_and_rescan_use_case_manages_root()
    {
        var repository = new FakeRoots();
        var scans = new FakeScans();
        var service = new ScanRootService(repository, scans);
        var root = Root("C:/Movies");
        await service.AddAsync(root, TestContext.Current.CancellationToken);

        await service.SetEnabledAsync(root.Id, false, TestContext.Current.CancellationToken);
        (await service.GetAllAsync(TestContext.Current.CancellationToken)).Single().Enabled.Should().BeFalse();
        await service.UpdateAsync(Root("C:/Updated", id: root.Id), TestContext.Current.CancellationToken);
        var result = await service.RescanAsync(root.Id, TestContext.Current.CancellationToken);
        result.Outcome.Should().Be(DiscoveryOutcome.Completed);
        scans.LastRoot!.Path.Should().Contain("Updated");

        await service.RemoveAsync(root.Id, TestContext.Current.CancellationToken);
        (await service.GetAllAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    private static ScanRoot Root(string path, Guid? id = null) =>
        ScanRoot.Create(id ?? Guid.NewGuid(), "Movies", path, ScanRootType.LocalFolder);

    private sealed class FakeRoots : IScanRootRepository
    {
        private readonly Dictionary<Guid, ScanRoot> _items = new();
        public Task<IReadOnlyList<ScanRoot>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScanRoot>>(_items.Values.ToArray());
        public Task SaveAsync(ScanRoot root, CancellationToken cancellationToken)
        {
            _items[root.Id] = root;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid rootId, CancellationToken cancellationToken)
        {
            _items.Remove(rootId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScans : IScanOrchestrator
    {
        public ScanRoot? LastRoot { get; private set; }
        public Task<DiscoveryResult> ScanAsync(ScanRoot root, CancellationToken cancellationToken = default)
        {
            LastRoot = root;
            return Task.FromResult(new DiscoveryResult(root.Id, DiscoveryOutcome.Completed, 0, 0, 0, 0, 0, 0, Array.Empty<string>()));
        }
    }
}
