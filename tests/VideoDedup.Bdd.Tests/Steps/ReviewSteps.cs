using Reqnroll;
using VideoDedup.Application;
using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Bdd.Tests.Steps;

[Binding]
public sealed class ReviewSteps
{
    private static readonly Guid Root = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Media = @"C:\Videos";
    private const string Fallback = @"C:\Users\user\AppData\Local\VideoDedup\Quarantine";
    private static readonly DateTimeOffset Modified = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryStorage _storage = new();
    private readonly InMemoryQuarantineLog _log = new();
    private readonly RecordingDecisionStore _decisions = new();
    private readonly RecordingMatches _matches = new();
    private readonly List<IndexedFileEntry> _entries = new();

    private IReadOnlyList<ReviewGroup> _groups = Array.Empty<ReviewGroup>();
    private QuarantinePlan _plan = new(Array.Empty<QuarantinePlanItem>());
    private QuarantineBatchResult _result = new(false, Array.Empty<QuarantineOutcome>());
    private IReadOnlyList<QuarantineOutcome> _undo = Array.Empty<QuarantineOutcome>();
    private WorkerStatusRegistry _registry = null!;

    private ReviewService Review => new(_matches, _decisions, new StaticCatalog(_entries), _log, _storage, SystemQueueClock.Instance);

    private QuarantineService Quarantine => new(new StaticCatalog(_entries), _storage, _log,
        new QuarantineServiceOptions(Fallback), SystemQueueClock.Instance);

    [Given("two copies of the same video are indexed and reachable")]
    public void GivenTwoReachableCopies()
    {
        Index("a.mkv");
        Index("b.mkv");
        _storage.SeedFile($@"{Media}\a.mkv");
        _storage.SeedFile($@"{Media}\b.mkv");
        _matches.Rows.Add(MatchPersistenceRecord.Create(FileA, FileB, MatchType.Exact, 1m,
            new EvidenceVersions(VisualAlgorithm.Version, AudioAlgorithm.Version), MatchPolicyOptions.PolicyVersion,
            ["SHA-256 content hashes are identical."]));
    }

    [Given("one copy has already been removed from the drive")]
    public void GivenOneCopyRemoved() => _storage.RemoveFile($@"{Media}\b.mkv");

    [Given("the drive holding the copies is offline")]
    public void GivenOfflineDrive() => _storage.RemoveDirectory(Media);

    [Given("I ask for the duplicate groups")]
    public void GivenGroups() => WhenIAskForGroups();

    [Given("probe and match workers are registered")]
    public void GivenWorkersRegistered() => _registry = new WorkerStatusRegistry(["Probe", "Match"]);

    [When("I ask for the duplicate groups")]
    public void WhenIAskForGroups() => _groups = Review.GetGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();

    [When(@"I mark one copy as (.*)")]
    public void WhenIMarkOneCopy(string decision)
    {
        var parsed = Enum.Parse<ReviewDecision>(decision, ignoreCase: true);
        Review.SetDecisionAsync(_groups[0].Key, FileA, parsed, CancellationToken.None).GetAwaiter().GetResult();
    }

    [When("I quarantine the group without confirming")]
    public void WhenQuarantiningWithoutConfirmation()
    {
        _plan = Quarantine.PlanAsync([FileA], CancellationToken.None).GetAwaiter().GetResult();
        _result = Quarantine.ExecuteAsync(_plan, confirmed: false, CancellationToken.None).GetAwaiter().GetResult();
    }

    [When("I quarantine the group after confirming")]
    public void WhenQuarantiningWithConfirmation()
    {
        _plan = Quarantine.PlanAsync([FileA], CancellationToken.None).GetAwaiter().GetResult();
        _result = Quarantine.ExecuteAsync(_plan, confirmed: true, CancellationToken.None).GetAwaiter().GetResult();
    }

    [When("I plan a quarantine of the remaining copy")]
    public void WhenPlanningRemainingCopy() =>
        _plan = Quarantine.PlanAsync([FileA], CancellationToken.None).GetAwaiter().GetResult();

    [When("I plan a quarantine of the first copy")]
    public void WhenPlanningFirstCopy() =>
        _plan = Quarantine.PlanAsync([FileA], CancellationToken.None).GetAwaiter().GetResult();

    [When("I restore the quarantined file")]
    public void WhenRestoring()
    {
        var recordId = _log.Records.Single().RecordId;
        _undo = Quarantine.UndoAsync([recordId], CancellationToken.None).GetAwaiter().GetResult();
    }

    [When("the workers are paused")]
    public void WhenWorkersPaused() => _registry.Pause();

    [When("the workers are resumed")]
    public void WhenWorkersResumed() => _registry.Resume();

    [Then("the group explains its match and lists both copies")]
    public void ThenGroupExplains()
    {
        var group = _groups.Should().ContainSingle().Subject;
        group.MatchType.Should().Be(MatchType.Exact);
        group.Files.Should().HaveCount(2);
        group.Reasons.Should().Contain(reason => reason.Contains("SHA-256", StringComparison.Ordinal));
        group.Files.Should().OnlyContain(file => file.FilePresent && file.RootAvailable);
    }

    [Then("the group suggests which copy to keep without acting on it")]
    public void ThenGroupSuggests()
    {
        var group = _groups.Single();
        group.RecommendedKeepFileId.Should().NotBeNull();
        group.RecommendationReason.Should().NotBeEmpty();
        _storage.Files.Should().HaveCount(2, "recommending a copy must never move anything");
    }

    [Then(@"the store records the (.*) decision for that copy")]
    public void ThenDecisionRecorded(string decision)
    {
        var parsed = Enum.Parse<ReviewDecision>(decision, ignoreCase: true);
        _decisions.Records.Should().ContainSingle().Which.Decision.Should().Be(parsed);
        _decisions.Records[0].FileId.Should().Be(FileA);
    }

    [Then("nothing moves and the plan asks for confirmation")]
    public void ThenNothingMoves()
    {
        _plan.RequiresConfirmation.Should().BeTrue();
        _result.ConfirmationRequired.Should().BeTrue();
        _result.Outcomes.Should().BeEmpty();
        _log.Records.Should().BeEmpty();
        _storage.Files.Should().ContainKey($@"{Media}\a.mkv");
    }

    [Then("the first copy is quarantined and the log records an undo entry")]
    public void ThenQuarantined()
    {
        _result.Moved.Should().ContainSingle().Which.FileId.Should().Be(FileA);
        _storage.Files.Should().NotContainKey($@"{Media}\a.mkv");
        var record = _log.Records.Should().ContainSingle().Subject;
        record.State.Should().Be(QuarantineState.Quarantined);
        record.DestinationPath.Should().Be(_plan.AllowedItems.Single().DestinationPath);
    }

    [Then("the copy is back at its original path and the log marks it restored")]
    public void ThenRestored()
    {
        _undo.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
        _storage.Files.Should().ContainKey($@"{Media}\a.mkv");
        _log.Records.Single().State.Should().Be(QuarantineState.Restored);
    }

    [Then("the plan blocks the copy because it is the last one")]
    public void ThenLastCopyBlocked()
    {
        var item = _plan.Items.Should().ContainSingle().Subject;
        item.IsAllowed.Should().BeFalse();
        item.BlockedBy.Should().Be(QuarantineBlockReason.LastRemainingCopy);
        _plan.RequiresConfirmation.Should().BeFalse();
    }

    [Then("the plan blocks the copy because the root is unavailable")]
    public void ThenRootUnavailableBlocked()
    {
        var item = _plan.Items.Should().ContainSingle().Subject;
        item.IsAllowed.Should().BeFalse();
        item.BlockedBy.Should().Be(QuarantineBlockReason.RootUnavailable);
    }

    [Then("every worker reports the paused state")]
    public void ThenPaused() =>
        _registry.Snapshot().Should().OnlyContain(status => status.State == WorkerRunState.Paused);

    [Then("every worker reports the idle state")]
    public void ThenIdle() =>
        _registry.Snapshot().Should().OnlyContain(status => status.State == WorkerRunState.Idle);

    private void Index(string relativePath) =>
        _entries.Add(new IndexedFileEntry(relativePath == "a.mkv" ? FileA : FileB, Root, "Videos", Media, true,
            relativePath, 1_000_000, Modified, ShaA, TimeSpan.FromMinutes(2), 1920, 1080, "h264", "aac", true, "matroska"));

    private sealed class StaticCatalog(IReadOnlyList<IndexedFileEntry> entries) : IIndexedFileCatalog
    {
        public Task<IReadOnlyList<IndexedFileEntry>> GetEntriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(entries);
    }

    private sealed class RecordingDecisionStore : IReviewDecisionStore
    {
        public List<ReviewDecisionRecord> Records { get; } = new();

        public Task SaveAsync(ReviewDecisionRecord record, CancellationToken cancellationToken)
        {
            Records.RemoveAll(existing => existing.GroupKey == record.GroupKey && existing.FileId == record.FileId);
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReviewDecisionRecord>> GetAllAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<ReviewDecisionRecord> snapshot = Records.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingMatches : IMatchResultRepository
    {
        public List<MatchPersistenceRecord> Rows { get; } = new();

        public Task SaveAsync(MatchPersistenceRecord record, CancellationToken cancellationToken)
        {
            Rows.Add(record);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid fileIdA, Guid fileIdB, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<MatchPersistenceRecord>> GetAllAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<MatchPersistenceRecord> snapshot = Rows.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class InMemoryQuarantineLog : IQuarantineLog
    {
        public List<QuarantineRecord> Records { get; } = new();

        public Task<QuarantineRecord> BeginAsync(Guid fileId, string sourcePath, string destinationPath, bool crossVolume,
            CancellationToken cancellationToken)
        {
            var record = QuarantineRecord.Create(Guid.NewGuid(), fileId, sourcePath, destinationPath,
                QuarantineState.Pending, crossVolume, DateTimeOffset.UtcNow);
            Records.Add(record);
            return Task.FromResult(record);
        }

        public Task MarkQuarantinedAsync(Guid recordId, CancellationToken cancellationToken) =>
            Transition(recordId, QuarantineState.Pending, QuarantineState.Quarantined);

        public Task MarkRestoredAsync(Guid recordId, CancellationToken cancellationToken) =>
            Transition(recordId, QuarantineState.Quarantined, QuarantineState.Restored);

        public Task MarkFailedAsync(Guid recordId, string reason, CancellationToken cancellationToken) =>
            Transition(recordId, QuarantineState.Pending, QuarantineState.Failed);

        public Task<IReadOnlyList<QuarantineRecord>> GetAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<QuarantineRecord> snapshot = Records.ToArray();
            return Task.FromResult(snapshot);
        }

        private Task Transition(Guid recordId, QuarantineState expected, QuarantineState target)
        {
            var index = Records.FindIndex(record => record.RecordId == recordId);
            if (index < 0 || Records[index].State != expected)
            {
                throw new InvalidOperationException($"Quarantine entry '{recordId:D}' is not in the expected {expected} state.");
            }

            Records[index] = Records[index].WithState(target, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryStorage : IQuarantineStorage
    {
        public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SeedFile(string path)
        {
            Files[path] = 1_000_000;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                SeedDirectory(directory);
            }
        }

        public void SeedDirectory(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current) && Directories.Add(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }
        }

        public void RemoveFile(string path) => Files.Remove(path);

        public void RemoveDirectory(string path) => Directories.Remove(Path.GetFullPath(path));

        public StorageInspection Inspect(string path)
        {
            if (Files.TryGetValue(path, out var size))
            {
                return new StorageInspection(true, false, false, size, DateTimeOffset.UtcNow);
            }

            return Directories.Contains(Path.GetFullPath(path))
                ? new StorageInspection(true, false, true, 0, null)
                : new StorageInspection(false, false, false, 0, null);
        }

        public bool DirectoryExists(string path) => Directories.Contains(Path.GetFullPath(path));

        public bool IsWritableDirectory(string path) => true;

        public string? GetVolumeRoot(string path) => Path.GetPathRoot(Path.GetFullPath(path));

        public void EnsureDirectory(string path) => SeedDirectory(path);

        public void Move(string source, string destination)
        {
            if (!Files.TryGetValue(source, out var size))
            {
                throw new StorageException(StorageFailure.NotFound, "The source file does not exist.");
            }

            if (Files.ContainsKey(destination))
            {
                throw new StorageException(StorageFailure.Conflict, "The destination already exists.");
            }

            Files[destination] = size;
            Files.Remove(source);
        }
    }
}
