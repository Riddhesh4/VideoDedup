using System.Runtime.CompilerServices;
using Reqnroll;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Bdd.Tests;

[Binding]
public sealed class DiscoverySteps
{
    private ScanRoot _root = null!;
    private FakeSource _source = null!;
    private FakeRepository _repository = null!;
    private IScanOrchestrator _orchestrator = null!;
    private DiscoveryResult _result = null!;
    private bool _candidate;
    private bool _singleFrameCandidate;
    private bool _unrelatedCandidate;

    [Given("a local root contains a supported video and a non-video file")]
    public void GivenLocalRootHasSupportedAndUnsupportedFiles()
    {
        _root = LocalRoot();
        _source = new FakeSource();
        _source.Files = [Observation("movie.mp4"), Observation("cover.jpg")];
        _repository = new FakeRepository();
        _orchestrator = new ScanOrchestrator(_source, _repository);
    }

    [When("I scan the configured root")]
    public async Task WhenScanConfiguredRoot() =>
        _result = await _orchestrator.ScanAsync(_root, TestContext.Current.CancellationToken);

    [Then("the video is recorded once and the non-video file is skipped")]
    public void ThenVideoIndexedOnly()
    {
        _result.AddedCount.Should().Be(1);
        _result.SkippedCount.Should().Be(1);
        _repository.Files.Should().ContainSingle().Which.RelativePath.Should().Be("movie.mp4");
    }

    [Given("a local root has already indexed a video")]
    public async Task GivenRootAlreadyIndexedVideo()
    {
        _root = LocalRoot();
        _source = new FakeSource { Files = [Observation("movie.mp4")] };
        _repository = new FakeRepository();
        _orchestrator = new ScanOrchestrator(_source, _repository);
        await _orchestrator.ScanAsync(_root, TestContext.Current.CancellationToken);
    }

    [When("the root becomes unavailable and I scan it again")]
    public async Task WhenRootUnavailableAndRescanned()
    {
        _source.Unavailable = true;
        _result = await _orchestrator.ScanAsync(_root, TestContext.Current.CancellationToken);
    }

    [Then("the scan reports the root unavailable and retains the indexed video")]
    public void ThenUnavailableRecordRetained()
    {
        _result.Outcome.Should().Be(DiscoveryOutcome.RootUnavailable);
        _repository.Files.Should().ContainSingle().Which.RelativePath.Should().Be("movie.mp4");
    }

    [Given("two versions of a video have similar fingerprints across three frames")]
    public void GivenSimilarVisualSequences()
    {
        var first = Enumerable.Range(0, 3).Select(i => Visual(TimeSpan.FromSeconds(i), (ulong)(0xFFUL << (i * 8)))).ToArray();
        var reencoded = first.Select(frame => Visual(frame.Timestamp, frame.PerceptualHash ^ 1)).ToArray();
        var unrelated = first.Select(frame => Visual(frame.Timestamp, ~frame.PerceptualHash)).ToArray();
        _candidate = VisualCandidatePolicy.IsCandidate(first, reencoded);
        _singleFrameCandidate = VisualCandidatePolicy.IsCandidate(first.Take(1).ToArray(), reencoded.Take(1).ToArray());
        _unrelatedCandidate = VisualCandidatePolicy.IsCandidate(first, unrelated);
    }

    [When("visual candidate evidence is evaluated")]
    public void WhenVisualEvidenceEvaluated() { }

    [Then("the versions are only a near-match candidate and one frame is insufficient")]
    public void ThenNearCandidateRequiresSequence()
    {
        _candidate.Should().BeTrue();
        _singleFrameCandidate.Should().BeFalse();
    }

    [Then("unrelated visual fingerprints do not become a candidate")]
    public void ThenUnrelatedVisualsRejected() => _unrelatedCandidate.Should().BeFalse();

    private static VisualFingerprint Visual(TimeSpan timestamp, ulong hash) =>
        VisualFingerprint.Create(timestamp, hash, 0.8m, VisualAlgorithm.Version);

    private static ScanRoot LocalRoot() =>
        ScanRoot.Create(Guid.NewGuid(), "BDD Root", "C:/BDD", ScanRootType.LocalFolder);

    private static FileObservation Observation(string path) =>
        new(path, 10, DateTimeOffset.Parse("2025-01-02T03:04:05Z"));

    private sealed class FakeSource : IFileDiscoverySource
    {
        public IReadOnlyList<FileObservation> Files { get; set; } = Array.Empty<FileObservation>();
        public bool Unavailable { get; set; }

        public Task<DriveDetails> GetDriveDetailsAsync(ScanRoot root, CancellationToken cancellationToken) =>
            Unavailable
                ? Task.FromException<DriveDetails>(new DirectoryNotFoundException())
                : Task.FromResult(DriveDetails.Create("bdd-volume", DriveKind.Local));

        public async IAsyncEnumerable<FileObservation> EnumerateFilesAsync(ScanRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeRepository : IDiscoveryRepository
    {
        private readonly Dictionary<string, IndexedFile> _files = new(StringComparer.OrdinalIgnoreCase);
        private string? _signature;
        public IReadOnlyCollection<IndexedFile> Files => _files.Values;

        public Task<IReadOnlyList<IndexedFile>> GetFilesAsync(Guid scanRootId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexedFile>>(_files.Values.ToArray());

        public Task<string?> GetVolumeSignatureAsync(Guid scanRootId, CancellationToken cancellationToken) =>
            Task.FromResult(_signature);

        public Task SaveFileAsync(IndexedFile file, CancellationToken cancellationToken)
        {
            _files[file.RelativePath] = file;
            return Task.CompletedTask;
        }

        public Task SetRootStatusAsync(Guid scanRootId, string? volumeSignature, RootAvailability availability, CancellationToken cancellationToken)
        {
            if (volumeSignature is not null) _signature = volumeSignature;
            return Task.CompletedTask;
        }
    }
}
