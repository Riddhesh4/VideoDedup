using VideoDedup.Application;
using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Application.Tests;

public sealed class ReviewFlowTests
{
    private static readonly Guid IdA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid IdB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid IdC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Root = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static MatchPersistenceRecord Pair(Guid first, Guid second, MatchType type, decimal score,
        decimal? visual = null, decimal? audio = null, decimal? offset = null, params string[] reasons) =>
        MatchPersistenceRecord.Create(first, second, type, score,
            new EvidenceVersions(VisualAlgorithm.Version, AudioAlgorithm.Version), MatchPolicyOptions.PolicyVersion,
            reasons.Length == 0 ? ["Shared evidence."] : reasons, offset, visual, audio);

    private static IndexedFileEntry Entry(Guid fileId, string relativePath, string? sha = ShaA, int? width = 1920,
        int? height = 1080, long size = 1_000_000, bool rootEnabled = true, int modifiedDay = 0, Guid? rootId = null) =>
        new(fileId, rootId ?? Root, "Videos", @"C:\Videos", rootEnabled, relativePath, size,
            new DateTimeOffset(2026, 01, 01, 0, 0, 0, TimeSpan.Zero).AddDays(modifiedDay), sha,
            TimeSpan.FromMinutes(2), width, height, "h264", "aac", true, "matroska");

    private static ReviewService Service(FakeMatchRepository matches, FakeDecisionStore decisions,
        FakeCatalog catalog, FakeQuarantineLog? log = null, FakeStorage? storage = null) =>
        new(matches, decisions, catalog, log, storage, new SystemQueueClock());

    [Fact]
    public async Task Pairs_sharing_a_file_form_one_group_with_the_strongest_headline_evidence()
    {
        var matches = new FakeMatchRepository();
        matches.Rows.Add(Pair(IdA, IdB, MatchType.NearDuplicate, 0.81m, 0.83m, 0.79m, reasons: "Visual fingerprint similarity: 83%."));
        matches.Rows.Add(Pair(IdB, IdC, MatchType.Exact, 1m));
        var service = Service(matches, new FakeDecisionStore(), new FakeCatalog(Entry(IdA, "a.mkv"), Entry(IdB, "b.mkv"), Entry(IdC, "c.mkv")));

        var groups = await service.GetGroupsAsync(Token);

        groups.Should().ContainSingle();
        var group = groups[0];
        group.Files.Select(file => file.FileId).Should().Equal(IdA, IdB, IdC);
        group.MatchType.Should().Be(MatchType.Exact);
        group.OverallScore.Should().Be(1m);
        group.Reasons.Should().Contain("Visual fingerprint similarity: 83%.");
        group.Key.Should().Be($"{IdA:D}|{IdB:D}|{IdC:D}");
    }

    [Fact]
    public async Task Live_reachability_and_quarantine_state_flow_into_every_copy()
    {
        var matches = new FakeMatchRepository();
        matches.Rows.Add(Pair(IdA, IdB, MatchType.Exact, 1m));
        var storage = new FakeStorage();
        storage.SeedFile(@"C:\Videos\a.mkv");
        var log = new FakeQuarantineLog();
        await log.BeginAsync(IdB, @"C:\Videos\b.mkv", @"D:\VideoDedup Quarantine\b.mkv", false, Token);
        var record = (await log.GetAsync(Token))[0];
        await log.MarkQuarantinedAsync(record.RecordId, Token);

        var groups = await Service(matches, new FakeDecisionStore(), new FakeCatalog(Entry(IdA, "a.mkv"), Entry(IdB, "b.mkv")),
            log, storage).GetGroupsAsync(Token);

        var files = groups.Single().Files;
        var present = files.Single(file => file.FileId == IdA);
        present.FilePresent.Should().BeTrue();
        present.CanBeActedOn.Should().BeTrue();
        var quarantined = files.Single(file => file.FileId == IdB);
        quarantined.IsQuarantined.Should().BeTrue();
        quarantined.FilePresent.Should().BeFalse();
        quarantined.CanBeActedOn.Should().BeFalse();
    }

    [Fact]
    public async Task Offline_roots_and_unsafe_paths_disable_actions()
    {
        var matches = new FakeMatchRepository();
        matches.Rows.Add(Pair(IdA, IdB, MatchType.Exact, 1m));
        var storage = new FakeStorage(); // No directories seeded: the root reads as offline.
        var groups = await Service(matches, new FakeDecisionStore(),
            new FakeCatalog(Entry(IdA, "a.mkv"), Entry(IdB, "../escape.mkv", rootEnabled: false)), log: null, storage)
            .GetGroupsAsync(Token);

        var files = groups.Single().Files;
        var offline = files.Single(file => file.FileId == IdA);
        offline.RootAvailable.Should().BeFalse();
        offline.CanBeActedOn.Should().BeFalse();
        var unsafePath = files.Single(file => file.FileId == IdB);
        unsafePath.HasSafePath.Should().BeFalse();
        unsafePath.RootEnabled.Should().BeFalse();
        unsafePath.CanBeActedOn.Should().BeFalse();
    }

    [Fact]
    public async Task Decisions_are_persisted_and_read_back_per_group_and_file()
    {
        var matches = new FakeMatchRepository();
        matches.Rows.Add(Pair(IdA, IdB, MatchType.NearDuplicate, 0.9m));
        var decisions = new FakeDecisionStore();
        var service = Service(matches, decisions, new FakeCatalog(Entry(IdA, "a.mkv"), Entry(IdB, "b.mkv")));

        await service.SetDecisionAsync($"{IdA:D}|{IdB:D}", IdA, ReviewDecision.Keep, Token);
        var groups = await service.GetGroupsAsync(Token);

        groups.Single().Files.Single(file => file.FileId == IdA).Decision.Should().Be(ReviewDecision.Keep);
        groups.Single().Files.Single(file => file.FileId == IdB).Decision.Should().Be(ReviewDecision.Pending);
        groups.Single().IsUndecided.Should().BeTrue();
        decisions.Saves.Should().ContainSingle();
    }

    [Fact]
    public async Task The_recommended_keep_copy_is_the_available_highest_quality_encode()
    {
        var matches = new FakeMatchRepository();
        matches.Rows.Add(Pair(IdA, IdB, MatchType.NearDuplicate, 0.9m));
        var storage = new FakeStorage();
        storage.SeedFile(@"C:\Videos\b.mkv");

        var groups = await Service(matches, new FakeDecisionStore(),
            new FakeCatalog(Entry(IdA, "a.mkv", width: 3840, height: 2160), Entry(IdB, "b.mkv", width: 1280, height: 720)),
            log: null, storage).GetGroupsAsync(Token);

        var group = groups.Single();
        group.RecommendedKeepFileId.Should().Be(IdB, "the richer 4K copy is not reachable, so it cannot be recommended");
        group.RecommendationReason.Should().Contain("1280x720");
    }

    [Fact]
    public async Task An_empty_match_store_produces_no_groups_and_no_catalog_reads()
    {
        var catalog = new FakeCatalog();
        var groups = await Service(new FakeMatchRepository(), new FakeDecisionStore(), catalog).GetGroupsAsync(Token);

        groups.Should().BeEmpty();
        catalog.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_file_removed_from_the_index_still_renders_as_blocked()
    {
        var matches = new FakeMatchRepository();
        matches.Rows.Add(Pair(IdA, IdB, MatchType.Exact, 1m));
        var groups = await Service(matches, new FakeDecisionStore(), new FakeCatalog(Entry(IdA, "a.mkv"))).GetGroupsAsync(Token);

        var missing = groups.Single().Files.Single(file => file.FileId == IdB);
        missing.FilePresent.Should().BeFalse();
        missing.RootAvailable.Should().BeFalse();
        missing.FullPath.Should().BeEmpty();
    }

    [Fact]
    public void Group_dto_validation_rejects_degenerate_inputs()
    {
        var files = new[]
        {
            new ReviewFileInfo(IdA, Root, "Videos", @"C:\Videos", "a.mkv", @"C:\Videos\a.mkv", true, true, true,
                true, false, false, 10, DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null, ReviewDecision.Pending),
        };

        FluentActions.Invoking(() => ReviewGroup.Create("key", MatchType.Exact, 1m, null, null, null, ["why"], files, null, "none"))
            .Should().Throw<ArgumentException>("a group needs at least two copies");
        FluentActions.Invoking(() => ReviewGroup.Create(" ", MatchType.Exact, 1m, null, null, null, ["why"], files, null, "none"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ReviewGroup.Create("key", MatchType.Exact, 1.5m, null, null, null, ["why"], files, null, "none"))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
