using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Workers;

public sealed class AudioFingerprintWorker(IJobQueue queue, IAudioProcessingRepository repository,
    IMediaProcessingRepository media, IMediaProbe probe, IAudioFingerprinter fingerprinter) : MediaWorker(queue, JobType.AudioFingerprint)
{
    protected override async Task ProcessAsync(MediaWorkItem item, CancellationToken cancellationToken)
    {
        var previous = await repository.GetAsync(item.FileId, cancellationToken).ConfigureAwait(false);
        if (previous?.IsCurrent(item.Revision) == true && previous.Failure is null) return;
        var stored = await media.GetAsync(item.FileId, cancellationToken).ConfigureAwait(false);
        var metadata = stored?.Revision == item.Revision ? stored.Metadata : null;
        metadata ??= (await probe.ProbeAsync(item.Path, item.Revision, cancellationToken).ConfigureAwait(false)).Metadata;
        var result = await fingerprinter.FingerprintAsync(item.Path, item.Revision, metadata, cancellationToken).ConfigureAwait(false);
        if (result.Revision != item.Revision) throw new MediaProcessingException(MediaFailure.Changed);
        var payload = result.Fingerprint?.Data ?? [];
        await repository.SaveAsync(AudioProcessingRecord.Create(item.FileId, item.Revision, result.Algorithm, result.Version,
            result.Duration, payload, result.HasAudio), cancellationToken).ConfigureAwait(false);
    }

    protected override Task SaveFailureAsync(MediaWorkItem item, MediaFailure failure, CancellationToken cancellationToken) =>
        repository.SaveAsync(AudioProcessingRecord.Create(item.FileId, item.Revision, AudioAlgorithm.Name,
            AudioAlgorithm.Version, TimeSpan.Zero, [], false, failure), cancellationToken);
}
