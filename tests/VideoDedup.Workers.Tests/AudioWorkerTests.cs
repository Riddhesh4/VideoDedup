using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Workers;

namespace VideoDedup.Workers.Tests;

public sealed class AudioWorkerTests
{
    private readonly IJobQueue _queue = Substitute.For<IJobQueue>();
    private readonly IAudioProcessingRepository _audio = Substitute.For<IAudioProcessingRepository>();
    private readonly IMediaProcessingRepository _media = Substitute.For<IMediaProcessingRepository>();
    private readonly IMediaProbe _probe = Substitute.For<IMediaProbe>();
    private readonly IAudioFingerprinter _fingerprinter = Substitute.For<IAudioFingerprinter>();
    private readonly MediaWorkItem _item = new(Guid.NewGuid(), Path.GetFullPath("audio ☃.mkv"), new(100, DateTimeOffset.UnixEpoch));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public AudioWorkerTests()
    {
        var job = new QueueJob(Guid.NewGuid(), _item.FileId, JobType.AudioFingerprint, JobState.Leased, 0, 0, 5,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test", DateTimeOffset.MaxValue, MediaWorker.Payload(_item), "root", null);
        _queue.ClaimAsync(Arg.Any<QueueClaimRequest>(), Arg.Any<CancellationToken>()).Returns([job]);
        _media.GetAsync(_item.FileId, Arg.Any<CancellationToken>()).Returns(new MediaProcessingRecord(_item.FileId, _item.Revision,
            new MediaMetadata(2, 2, 64, 48, "ffv1", "pcm_s16le", true, 24, "matroska", null, "yuv420p", null, 8), new string('a', 64)));
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<FileRevision>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(_item.Revision,
            new MediaMetadata(2, 2, 64, 48, "ffv1", "pcm_s16le", true, 24, "matroska", null, "yuv420p", null, 8)));
    }

    private AudioFingerprintWorker Worker() => new(_queue, _audio, _media, _probe, _fingerprinter);

    [Fact]
    public async Task Successful_result_persists_but_does_not_overwrite_exact_or_visual_results()
    {
        var fp = AudioFingerprint.Create(TimeSpan.Zero, TimeSpan.FromSeconds(2), [1, 2, 3], AudioAlgorithm.Version);
        _fingerprinter.FingerprintAsync(_item.Path, _item.Revision, Arg.Any<MediaMetadata>(), Arg.Any<CancellationToken>())
            .Returns(new AudioFingerprintResult(_item.Revision, TimeSpan.FromSeconds(2), fp, AudioAlgorithm.Name, AudioAlgorithm.Version, true));
        await Worker().RunOnceAsync(Token);
        await _audio.Received(1).SaveAsync(Arg.Is<AudioProcessingRecord>(r => r.HasAudio && r.Fingerprint.SequenceEqual(new byte[] { 1, 2, 3 })), Arg.Any<CancellationToken>());
        await _media.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
        await _queue.Received(1).CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_audio_is_normal_skip_and_does_not_fail_visual_processing()
    {
        _fingerprinter.FingerprintAsync(_item.Path, _item.Revision, Arg.Any<MediaMetadata>(), Arg.Any<CancellationToken>())
            .Returns(new AudioFingerprintResult(_item.Revision, TimeSpan.Zero, null, AudioAlgorithm.Name, AudioAlgorithm.Version, false));
        await Worker().RunOnceAsync(Token);
        await _audio.Received(1).SaveAsync(Arg.Is<AudioProcessingRecord>(r => !r.HasAudio && r.Fingerprint.Length == 0 && r.Failure == null), Arg.Any<CancellationToken>());
        await _queue.Received(1).CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, default!, Arg.Any<CancellationToken>());
        await _media.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(MediaFailure.InvalidData, false)]
    [InlineData(MediaFailure.Timeout, true)]
    [InlineData(MediaFailure.Changed, true)]
    public async Task Audio_failure_is_persisted_independently_and_only_retryable_failures_retry(MediaFailure failure, bool retry)
    {
        _fingerprinter.FingerprintAsync(Arg.Any<string>(), Arg.Any<FileRevision>(), Arg.Any<MediaMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AudioFingerprintResult>(new MediaProcessingException(failure)));
        await Worker().RunOnceAsync(Token);
        await _audio.Received(1).SaveAsync(Arg.Is<AudioProcessingRecord>(r => r.Failure == failure && !r.HasAudio), Arg.Any<CancellationToken>());
        await _media.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
        if (retry) await _queue.Received(1).FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), failure.ToString(), Arg.Any<CancellationToken>());
        else await _queue.Received(1).CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_leaves_job_leased_without_persisting_partial_audio()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _fingerprinter.FingerprintAsync(Arg.Any<string>(), Arg.Any<FileRevision>(), Arg.Any<MediaMetadata>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancel.Cancel();
            return Task.FromCanceled<AudioFingerprintResult>(cancel.Token);
        });
        await FluentActions.Awaiting(() => Worker().RunOnceAsync(cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        await _audio.DidNotReceiveWithAnyArgs().SaveAsync(default!, Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default, default!, Arg.Any<CancellationToken>());
    }
}
