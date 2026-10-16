using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Workers;

namespace VideoDedup.Workers.Tests;

public sealed class MediaWorkerTests
{
    [Theory]
    [InlineData(MediaFailure.Unsupported, false)]
    [InlineData(MediaFailure.InvalidData, false)]
    [InlineData(MediaFailure.Changed, true)]
    [InlineData(MediaFailure.Timeout, true)]
    public async Task Typed_failure_is_persisted_and_either_completed_or_retried(MediaFailure failure, bool retry)
    {
        var queue = Substitute.For<IJobQueue>();
        var repository = Substitute.For<IMediaProcessingRepository>();
        var probe = Substitute.For<IMediaProbe>();
        var item = new MediaWorkItem(Guid.NewGuid(), Path.GetFullPath("clip.mp4"), new FileRevision(10, DateTimeOffset.UnixEpoch));
        var job = Job(item.FileId, MediaWorker.Payload(item));
        queue.ClaimAsync(Arg.Any<QueueClaimRequest>(), Arg.Any<CancellationToken>()).Returns(new[] { job });
        probe.ProbeAsync(item.Path, item.Revision, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ProbeResult>(new MediaProcessingException(failure)));
        await new ProbeWorker(queue, repository, probe).RunOnceAsync(TestContext.Current.CancellationToken);
        await repository.Received(1).SaveAsync(Arg.Is<MediaProcessingRecord>(r => r.Failure == failure && r.Sha256 == null), Arg.Any<CancellationToken>());
        if (retry) await queue.Received(1).FailAsync(job.Id, Arg.Any<string>(), failure.ToString(), Arg.Any<CancellationToken>());
        else await queue.Received(1).CompleteAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"work\":null}")]
    [InlineData("{\"version\":1,\"work\":{\"Revision\":null}}")]
    public async Task Invalid_work_payload_is_terminal_without_invoking_probe(string payload)
    {
        var queue = Substitute.For<IJobQueue>();
        var probe = Substitute.For<IMediaProbe>();
        var repository = Substitute.For<IMediaProcessingRepository>();
        var job = Job(Guid.NewGuid(), payload);
        queue.ClaimAsync(Arg.Any<QueueClaimRequest>(), Arg.Any<CancellationToken>()).Returns(new[] { job });
        await new ProbeWorker(queue, repository, probe).RunOnceAsync(TestContext.Current.CancellationToken);
        await queue.Received(1).CompleteAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default!, Arg.Any<CancellationToken>());
        await repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_does_not_acknowledge_unfinished_work()
    {
        var queue = Substitute.For<IJobQueue>();
        var repository = Substitute.For<IMediaProcessingRepository>();
        var probe = Substitute.For<IMediaProbe>();
        var item = new MediaWorkItem(Guid.NewGuid(), Path.GetFullPath("clip.mp4"), new FileRevision(10, DateTimeOffset.UnixEpoch));
        queue.ClaimAsync(Arg.Any<QueueClaimRequest>(), Arg.Any<CancellationToken>()).Returns(new[] { Job(item.FileId, MediaWorker.Payload(item)) });
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<FileRevision>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancel.Cancel();
            return Task.FromCanceled<ProbeResult>(cancel.Token);
        });
        await FluentActions.Awaiting(() => new ProbeWorker(queue, repository, probe).RunOnceAsync(cancel.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await queue.DidNotReceiveWithAnyArgs().CompleteAsync(default, default!, Arg.Any<CancellationToken>());
        await repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
    }

    private static QueueJob Job(Guid fileId, string payload) => new(Guid.NewGuid(), fileId, JobType.Probe, JobState.Leased,
        0, 0, 5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test", DateTimeOffset.MaxValue, payload, null, null);
}
