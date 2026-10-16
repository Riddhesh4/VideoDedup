using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Workers;

namespace VideoDedup.Workers.Tests;

public sealed class VisualWorkerTests
{
    private readonly IJobQueue _queue = Substitute.For<IJobQueue>();
    private readonly IMediaProcessingRepository _media = Substitute.For<IMediaProcessingRepository>();
    private readonly IVisualProcessingRepository _visuals = Substitute.For<IVisualProcessingRepository>();
    private readonly IMediaProbe _probe = Substitute.For<IMediaProbe>();
    private readonly IVisualFingerprinter _fingerprinter = Substitute.For<IVisualFingerprinter>();
    private readonly MediaWorkItem _item = new(Guid.NewGuid(), Path.GetFullPath("visual.mkv"), new(123, DateTimeOffset.UnixEpoch));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private VisualFingerprintWorker Worker => new(_queue, _media, _visuals, _probe, _fingerprinter);
    public VisualWorkerTests()
    {
        var job = new QueueJob(Guid.NewGuid(), _item.FileId, JobType.VisualFingerprint, JobState.Leased, 0, 0, 5,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test", DateTimeOffset.MaxValue, MediaWorker.Payload(_item), null, null);
        _queue.ClaimAsync(Arg.Any<QueueClaimRequest>(), Arg.Any<CancellationToken>()).Returns(new[] { job });
        var metadata = new MediaMetadata(1, 1, 64, 48, "ffv1", null, false, 1, null, null, null, null, null);
        _media.GetAsync(_item.FileId, Arg.Any<CancellationToken>()).Returns(new MediaProcessingRecord(_item.FileId, _item.Revision, metadata));
    }
    [Theory]
    [InlineData(MediaFailure.InvalidData, false)]
    [InlineData(MediaFailure.Unsupported, false)]
    [InlineData(MediaFailure.Timeout, true)]
    [InlineData(MediaFailure.Changed, true)]
    public async Task Visual_failure_preserves_metadata_and_hash_and_retries_only_transient_failures(MediaFailure failure, bool retry)
    {
        _fingerprinter.FingerprintAsync(Arg.Any<string>(), Arg.Any<FileRevision>(), Arg.Any<MediaMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<VisualFingerprintResult>(new MediaProcessingException(failure)));
        await Worker.RunOnceAsync(Token);
        await _visuals.Received(1).SaveAsync(Arg.Is<VisualProcessingRecord>(r => r.Failure == failure && r.Frames.Count == 0), Arg.Any<CancellationToken>());
        await _media.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
        if (retry) await _queue.Received(1).FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), failure.ToString(), Arg.Any<CancellationToken>());
        else await _queue.Received(1).CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task Current_results_skip_decode_but_algorithm_changes_rebuild()
    {
        var frame = VisualFingerprint.Create(TimeSpan.Zero, 1, 1, VisualAlgorithm.Version);
        var record = new VisualProcessingRecord(_item.FileId, _item.Revision, VisualAlgorithm.Name, VisualAlgorithm.Version,
            TimeSpan.FromSeconds(10).Ticks, 60, [frame]);
        _visuals.GetAsync(_item.FileId, Arg.Any<CancellationToken>()).Returns(record);
        await Worker.RunOnceAsync(Token);
        await _fingerprinter.DidNotReceiveWithAnyArgs().FingerprintAsync(default!, default!, default!, Arg.Any<CancellationToken>());
        _visuals.GetAsync(_item.FileId, Arg.Any<CancellationToken>()).Returns(record with { Version = FingerprintVersion.Create(2, 0) });
        _fingerprinter.FingerprintAsync(Arg.Any<string>(), Arg.Any<FileRevision>(), Arg.Any<MediaMetadata>(), Arg.Any<CancellationToken>())
            .Returns(new VisualFingerprintResult(_item.Revision, [frame]));
        await Worker.RunOnceAsync(Token);
        await _visuals.Received(1).SaveAsync(Arg.Is<VisualProcessingRecord>(r => r.Version == VisualAlgorithm.Version && r.Frames.Count == 1), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task Cancellation_leaves_lease_and_does_not_save_partial_fingerprints()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _fingerprinter.FingerprintAsync(Arg.Any<string>(), Arg.Any<FileRevision>(), Arg.Any<MediaMetadata>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancel.Cancel(); return Task.FromCanceled<VisualFingerprintResult>(cancel.Token);
        });
        await FluentActions.Awaiting(() => Worker.RunOnceAsync(cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default, default!, Arg.Any<CancellationToken>());
        await _visuals.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
    }
}
