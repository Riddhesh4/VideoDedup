using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Application.Tests;

public sealed class QuarantineFlowTests
{
    private static readonly Guid IdA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid IdB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Root = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Fallback = @"C:\Users\me\AppData\Local\VideoDedup\Quarantine";

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static IndexedFileEntry Entry(Guid fileId, string relativePath, string? sha = ShaA, bool rootEnabled = true) =>
        new(fileId, Root, "Videos", @"C:\Videos", rootEnabled, relativePath, 1_000_000,
            new DateTimeOffset(2026, 01, 01, 0, 0, 0, TimeSpan.Zero), sha, TimeSpan.FromMinutes(2),
            1920, 1080, "h264", "aac", true, "matroska");

    private static FakeCatalog Copies() => new(Entry(IdA, "a.mkv"), Entry(IdB, "b.mkv"));

    private static QuarantineService Service(FakeCatalog catalog, FakeStorage storage, FakeQuarantineLog log,
        int maxItems = 500) =>
        new(catalog, storage, log, new QuarantineServiceOptions(Fallback, maxItems), new SystemQueueClock());

    private static FakeStorage StorageWithBothCopies()
    {
        var storage = new FakeStorage();
        storage.SeedFile(@"C:\Videos\a.mkv");
        storage.SeedFile(@"C:\Videos\b.mkv");
        return storage;
    }

    [Fact]
    public async Task A_copy_with_another_reachable_copy_is_planned_into_a_same_volume_folder()
    {
        var storage = StorageWithBothCopies();
        var plan = await Service(Copies(), storage, new FakeQuarantineLog()).PlanAsync([IdA], Token);

        var item = plan.Items.Should().ContainSingle().Subject;
        item.IsAllowed.Should().BeTrue();
        item.BlockedBy.Should().BeNull();
        item.DestinationPath.Should().Be(@"C:\VideoDedup Quarantine\a-aaaaaaaa.mkv");
        item.CrossVolume.Should().BeFalse();
        plan.RequiresConfirmation.Should().BeTrue();
        plan.SpansVolumes.Should().BeFalse();
    }

    [Fact]
    public async Task The_last_remaining_copy_is_blocked()
    {
        var storage = StorageWithBothCopies();
        storage.RemoveFile(@"C:\Videos\b.mkv");

        var plan = await Service(Copies(), storage, new FakeQuarantineLog()).PlanAsync([IdA], Token);

        var item = plan.Items.Should().ContainSingle().Subject;
        item.IsAllowed.Should().BeFalse();
        item.BlockedBy.Should().Be(QuarantineBlockReason.LastRemainingCopy);
        item.DestinationPath.Should().BeNull();
        plan.RequiresConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task A_copy_without_a_content_hash_cannot_prove_another_copy_exists()
    {
        var storage = StorageWithBothCopies();
        var catalog = new FakeCatalog(Entry(IdA, "a.mkv", sha: null), Entry(IdB, "b.mkv", sha: null));

        var plan = await Service(catalog, storage, new FakeQuarantineLog()).PlanAsync([IdA], Token);

        plan.Items.Single().BlockedBy.Should().Be(QuarantineBlockReason.LastRemainingCopy);
        plan.Items.Single().Reasons.Should().Contain(reason => reason.Contains("no content hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Offline_roots_missing_files_disabled_roots_and_unsafe_paths_are_all_blocked()
    {
        var offline = new FakeStorage();
        var offlinePlan = await Service(Copies(), offline, new FakeQuarantineLog()).PlanAsync([IdA], Token);
        offlinePlan.Items.Single().BlockedBy.Should().Be(QuarantineBlockReason.RootUnavailable);

        var directoryOnly = new FakeStorage();
        directoryOnly.SeedDirectory(@"C:\Videos");
        directoryOnly.SeedFile(@"C:\Videos\b.mkv");
        var missingPlan = await Service(Copies(), directoryOnly, new FakeQuarantineLog()).PlanAsync([IdA], Token);
        missingPlan.Items.Single().BlockedBy.Should().Be(QuarantineBlockReason.FileMissing);

        var disabledPlan = await Service(new FakeCatalog(Entry(IdA, "a.mkv", rootEnabled: false), Entry(IdB, "b.mkv", rootEnabled: false)),
            StorageWithBothCopies(), new FakeQuarantineLog()).PlanAsync([IdA], Token);
        disabledPlan.Items.Single().BlockedBy.Should().Be(QuarantineBlockReason.RootDisabled);

        var escapePlan = await Service(new FakeCatalog(Entry(IdA, "../escape.mkv"), Entry(IdB, "b.mkv")),
            StorageWithBothCopies(), new FakeQuarantineLog()).PlanAsync([IdA], Token);
        escapePlan.Items.Single().BlockedBy.Should().Be(QuarantineBlockReason.PathOutsideRoot);
    }

    [Fact]
    public async Task A_file_that_left_the_index_is_blocked_instead_of_silently_skipped()
    {
        var plan = await Service(Copies(), StorageWithBothCopies(), new FakeQuarantineLog())
            .PlanAsync([Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")], Token);

        var item = plan.Items.Should().ContainSingle().Subject;
        item.IsAllowed.Should().BeFalse();
        item.BlockedBy.Should().Be(QuarantineBlockReason.FileMissing);
    }

    [Fact]
    public async Task Nothing_moves_without_explicit_confirmation()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var service = Service(Copies(), storage, log);
        var plan = await service.PlanAsync([IdA], Token);

        var result = await service.ExecuteAsync(plan, confirmed: false, Token);

        result.ConfirmationRequired.Should().BeTrue();
        result.Outcomes.Should().BeEmpty();
        storage.Moves.Should().BeEmpty();
        log.Records.Should().BeEmpty();
        storage.Files.Should().ContainKey(@"C:\Videos\a.mkv");
    }

    [Fact]
    public async Task Confirmed_execution_moves_the_file_and_records_the_durable_log_row()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var service = Service(Copies(), storage, log);
        var plan = await service.PlanAsync([IdA], Token);

        var result = await service.ExecuteAsync(plan, confirmed: true, Token);

        result.ConfirmationRequired.Should().BeFalse();
        var moved = result.Moved.Should().ContainSingle().Subject;
        moved.FileId.Should().Be(IdA);
        moved.RecordId.Should().NotBeNull();
        storage.Files.Should().NotContainKey(@"C:\Videos\a.mkv");
        storage.Files.Keys.Should().Contain(@"C:\VideoDedup Quarantine\a-aaaaaaaa.mkv");
        var record = log.Records.Should().ContainSingle().Subject;
        record.State.Should().Be(QuarantineState.Quarantined);
        record.CompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task A_confirmed_batch_never_empties_a_group_of_identical_copies()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var service = Service(Copies(), storage, log);
        var plan = await service.PlanAsync([IdA, IdB], Token);
        plan.AllowedItems.Should().HaveCount(2);

        var result = await service.ExecuteAsync(plan, confirmed: true, Token);

        result.Moved.Should().ContainSingle();
        var blocked = result.Failures.Should().ContainSingle().Subject;
        blocked.FileId.Should().Be(IdB);
        blocked.Failure.Should().Contain("last copy");
        storage.Files.Keys.Should().Contain(@"C:\Videos\b.mkv");
        log.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unknown_entry_is_not_quarantined()
    {
        var storage = StorageWithBothCopies();
        var service = Service(Copies(), storage, new FakeQuarantineLog());
        var unknown = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var plan = await service.PlanAsync([IdA, unknown], Token);

        var result = await service.ExecuteAsync(plan, confirmed: true, Token);

        result.Moved.Should().ContainSingle();
        result.Failures.Should().ContainSingle().Which.FileId.Should().Be(unknown);
    }

    [Fact]
    public async Task A_denied_move_is_reported_with_a_typed_reason_and_the_log_row_fails()
    {
        var storage = StorageWithBothCopies();
        storage.DeniedPath = @"C:\Videos\a.mkv";
        var log = new FakeQuarantineLog();
        var service = Service(Copies(), storage, log);
        var plan = await service.PlanAsync([IdA], Token);

        var result = await service.ExecuteAsync(plan, confirmed: true, Token);

        var failure = result.Failures.Should().ContainSingle().Subject;
        failure.Failure.Should().Contain("denied");
        log.Records.Should().ContainSingle().Which.State.Should().Be(QuarantineState.Failed);
        storage.Files.Should().ContainKey(@"C:\Videos\a.mkv");
    }

    [Fact]
    public async Task A_destination_collision_gets_a_numbered_suffix_without_overwriting()
    {
        var storage = StorageWithBothCopies();
        storage.SeedFile(@"C:\VideoDedup Quarantine\a-aaaaaaaa.mkv", size: 42);
        var service = Service(Copies(), storage, new FakeQuarantineLog());
        var plan = await service.PlanAsync([IdA], Token);

        await service.ExecuteAsync(plan, confirmed: true, Token);

        storage.Files[@"C:\VideoDedup Quarantine\a-aaaaaaaa.mkv"].Should().Be(42, "an existing file is never overwritten");
        storage.Files.Keys.Should().Contain(@"C:\VideoDedup Quarantine\a-aaaaaaaa-2.mkv");
    }

    [Fact]
    public async Task Cross_volume_destinations_are_flagged_and_an_unwritable_same_volume_folder_falls_back()
    {
        var storage = StorageWithBothCopies();
        storage.UnwritableDirectories.Add(@"C:\VideoDedup Quarantine");
        storage.VolumeRoots[@"C:\Users"] = @"D:\";
        // The fallback is on a different volume than the source, which is exactly the case the UI must warn about.
        storage.VolumeRoots[@"C:\Videos"] = @"C:\";
        storage.VolumeRoots[@"C:\VideoDedup Quarantine"] = @"C:\";
        var service = Service(Copies(), storage, new FakeQuarantineLog());

        var plan = await service.PlanAsync([IdA], Token);

        var item = plan.Items.Should().ContainSingle().Subject;
        item.IsAllowed.Should().BeTrue(string.Join(" | ", item.Reasons));
        item.CrossVolume.Should().BeTrue();
        item.DestinationPath.Should().StartWith(@"C:\Users\me\AppData\Local\VideoDedup\Quarantine");
        item.Reasons.Should().Contain(reason => reason.Contains("not writable", StringComparison.OrdinalIgnoreCase));
        plan.SpansVolumes.Should().BeTrue();
    }

    [Fact]
    public async Task Undo_restores_the_original_path_and_marks_the_log_row_restored()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var service = Service(Copies(), storage, log);
        var plan = await service.PlanAsync([IdA], Token);
        var moved = (await service.ExecuteAsync(plan, confirmed: true, Token)).Moved.Single();

        var outcomes = await service.UndoAsync([moved.RecordId!.Value], Token);

        var restore = outcomes.Should().ContainSingle().Subject;
        restore.Succeeded.Should().BeTrue();
        storage.Files.Should().ContainKey(@"C:\Videos\a.mkv");
        storage.Moves.Should().HaveCount(2);
        log.Records.Single().State.Should().Be(QuarantineState.Restored);
    }

    [Fact]
    public async Task A_second_undo_of_the_same_entry_is_refused_with_an_explanation()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var service = Service(Copies(), storage, log);
        var plan = await service.PlanAsync([IdA], Token);
        var moved = (await service.ExecuteAsync(plan, confirmed: true, Token)).Moved.Single();

        (await service.UndoAsync([moved.RecordId!.Value], Token)).Single().Succeeded.Should().BeTrue();
        var restoredTwice = (await service.UndoAsync([moved.RecordId!.Value], Token)).Single();

        restoredTwice.Succeeded.Should().BeFalse();
        restoredTwice.Failure.Should().Contain("already restored");
    }

    [Fact]
    public async Task Undo_reports_a_missing_quarantine_copy()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var service = Service(Copies(), storage, log);
        var plan = await service.PlanAsync([IdA], Token);
        var destination = plan.AllowedItems.Single().DestinationPath!;
        var moved = (await service.ExecuteAsync(plan, confirmed: true, Token)).Moved.Single();
        storage.RemoveFile(destination);

        var outcome = (await service.UndoAsync([moved.RecordId!.Value], Token)).Single();

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Contain("missing");
        log.Records.Single().State.Should().Be(QuarantineState.Quarantined);
    }

    [Fact]
    public async Task Undo_reports_an_occupied_original_path_without_overwriting()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var service = Service(Copies(), storage, log);
        var plan = await service.PlanAsync([IdA], Token);
        var moved = (await service.ExecuteAsync(plan, confirmed: true, Token)).Moved.Single();
        storage.SeedFile(@"C:\Videos\a.mkv", size: 7);

        var outcome = (await service.UndoAsync([moved.RecordId!.Value], Token)).Single();

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Contain("occupied");
        storage.Files[@"C:\Videos\a.mkv"].Should().Be(7);
        log.Records.Single().State.Should().Be(QuarantineState.Quarantined);
    }

    [Fact]
    public async Task Undo_reports_an_unknown_entry_and_rejects_an_empty_selection()
    {
        var service = Service(Copies(), StorageWithBothCopies(), new FakeQuarantineLog());

        var outcome = (await service.UndoAsync([Guid.NewGuid()], Token)).Single();
        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Contain("no longer exists");

        await FluentActions.Awaiting(() => service.UndoAsync([], Token)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Recovery_resolves_interrupted_intents_and_flags_missing_quarantine_copies()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var interruptedSource = await log.BeginAsync(IdA, @"C:\Videos\a.mkv", @"C:\VideoDedup Quarantine\a.mkv", false, Token);
        var completedCopy = await log.BeginAsync(IdB, @"C:\Videos\b.mkv", @"C:\VideoDedup Quarantine\b.mkv", false, Token);
        storage.RemoveFile(@"C:\Videos\b.mkv");
        storage.SeedFile(@"C:\VideoDedup Quarantine\b.mkv");
        var lost = await log.BeginAsync(IdA, @"C:\Videos\a.mkv", @"C:\VideoDedup Quarantine\lost.mkv", false, Token);
        await log.MarkQuarantinedAsync(lost.RecordId, Token);

        var report = await Service(Copies(), storage, log).RecoverInterruptedAsync(Token);

        report.Resolved.Select(record => record.RecordId).Should().BeEquivalentTo([interruptedSource.RecordId, completedCopy.RecordId]);
        log.Records.Single(record => record.RecordId == interruptedSource.RecordId).State.Should().Be(QuarantineState.Failed);
        log.Records.Single(record => record.RecordId == interruptedSource.RecordId).FailureReason
            .Should().Contain("still in place");
        log.Records.Single(record => record.RecordId == completedCopy.RecordId).State.Should().Be(QuarantineState.Quarantined);
        report.NeedsAttention.Should().ContainSingle().Which.RecordId.Should().Be(lost.RecordId);
    }

    [Fact]
    public async Task Recovery_flags_an_interruption_where_both_or_neither_copy_exists()
    {
        var storage = StorageWithBothCopies();
        var log = new FakeQuarantineLog();
        var both = await log.BeginAsync(IdA, @"C:\Videos\a.mkv", @"C:\VideoDedup Quarantine\a.mkv", false, Token);
        storage.SeedFile(@"C:\VideoDedup Quarantine\a.mkv");
        var neither = await log.BeginAsync(IdB, @"C:\Videos\b.mkv", @"C:\VideoDedup Quarantine\missing.mkv", false, Token);
        storage.RemoveFile(@"C:\Videos\b.mkv");

        var report = await Service(Copies(), storage, log).RecoverInterruptedAsync(Token);

        report.Resolved.Should().HaveCount(2);
        log.Records.Single(record => record.RecordId == both.RecordId).FailureReason.Should().Contain("Both the original");
        log.Records.Single(record => record.RecordId == neither.RecordId).FailureReason.Should().Contain("Neither");
    }

    [Fact]
    public async Task Selection_and_configuration_bounds_are_enforced()
    {
        var service = Service(Copies(), StorageWithBothCopies(), new FakeQuarantineLog(), maxItems: 1);

        await FluentActions.Awaiting(() => service.PlanAsync([], Token)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => service.PlanAsync([IdA, IdB], Token)).Should().ThrowAsync<ArgumentException>();
        FluentActions.Invoking(() => new QuarantineService(Copies(), StorageWithBothCopies(), new FakeQuarantineLog(),
                new QuarantineServiceOptions("relative")))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new QuarantineServiceOptions(Fallback, 0).Validate())
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Quarantine_records_reject_degenerate_inputs()
    {
        var id = Guid.NewGuid();
        FluentActions.Invoking(() => QuarantineRecord.Create(Guid.Empty, id, @"C:\a.mkv", @"D:\b.mkv",
                QuarantineState.Pending, false, DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => QuarantineRecord.Create(Guid.NewGuid(), id, @"C:\a.mkv", @"C:\a.mkv",
                QuarantineState.Pending, false, DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => QuarantineRecord.Create(Guid.NewGuid(), id, "relative.mkv", @"D:\b.mkv",
                QuarantineState.Pending, false, DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => QuarantineRecord.Create(Guid.NewGuid(), id, @"C:\a.mkv", @"D:\b.mkv",
                QuarantineState.Pending, false, DateTimeOffset.UtcNow, failureReason: new string('x', 900)))
            .Should().NotThrow();
    }
}
