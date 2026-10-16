using VideoDedup.Domain;

namespace VideoDedup.Application;

public sealed record AudioFingerprintResult(FileRevision Revision, TimeSpan Duration, AudioFingerprint? Fingerprint,
    string Algorithm, FingerprintVersion Version, bool HasAudio);

public interface IAudioFingerprinter
{
    Task<AudioFingerprintResult> FingerprintAsync(string path, FileRevision expected, MediaMetadata metadata, CancellationToken cancellationToken);
}

public sealed record AudioProcessingRecord
{
    private readonly byte[] _fingerprint;

    private AudioProcessingRecord(Guid fileId, FileRevision revision, string algorithm, FingerprintVersion version,
        TimeSpan duration, byte[] fingerprint, bool hasAudio, MediaFailure? failure)
    {
        FileId = fileId;
        Revision = revision;
        Algorithm = algorithm;
        Version = version;
        Duration = duration;
        _fingerprint = (byte[])fingerprint.Clone();
        HasAudio = hasAudio;
        Failure = failure;
    }

    public Guid FileId { get; }
    public FileRevision Revision { get; }
    public string Algorithm { get; }
    public FingerprintVersion Version { get; }
    public TimeSpan Duration { get; }
    public byte[] Fingerprint => (byte[])_fingerprint.Clone();
    public bool HasAudio { get; }
    public MediaFailure? Failure { get; }

    public static AudioProcessingRecord Create(Guid fileId, FileRevision revision, string algorithm, FingerprintVersion version,
        TimeSpan duration, ReadOnlySpan<byte> fingerprint, bool hasAudio, MediaFailure? failure = null) =>
        new(fileId, revision, algorithm, version, duration, fingerprint.ToArray(), hasAudio, failure);

    public bool IsCurrent(FileRevision revision) => Revision == revision &&
        Algorithm == AudioAlgorithm.Name && Version == AudioAlgorithm.Version;
}

public interface IAudioProcessingRepository
{
    Task SaveAsync(AudioProcessingRecord record, CancellationToken cancellationToken);
    Task<AudioProcessingRecord?> GetAsync(Guid fileId, CancellationToken cancellationToken);
}

public static class AudioAlgorithm
{
    public const string Name = "chromaprint-fpcalc";
    // fpcalc CLI JSON uses Chromaprint's compressed URL-safe Base64 representation.
    public static FingerprintVersion Version { get; } = FingerprintVersion.Create(1, 0);
}
