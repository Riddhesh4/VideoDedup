namespace VideoDedup.Domain;

/// <summary>Indexed media-file metadata. Unknown probe values remain null rather than fabricated.</summary>
public sealed record VideoFile
{
    private VideoFile(Guid id, Guid scanRootId, string relativePath, long sizeBytes, DateTimeOffset modifiedUtc,
        ScanState scanState, TimeSpan? duration, int? width, int? height, string? videoCodec,
        string? audioCodec, bool? hasAudio, string? sha256, IReadOnlyList<VisualFingerprint> visualFingerprints)
    {
        Id = id;
        ScanRootId = scanRootId;
        RelativePath = relativePath;
        SizeBytes = sizeBytes;
        ModifiedUtc = modifiedUtc.ToUniversalTime();
        ScanState = scanState;
        Duration = duration;
        Width = width;
        Height = height;
        VideoCodec = videoCodec;
        AudioCodec = audioCodec;
        HasAudio = hasAudio;
        Sha256 = sha256;
        VisualFingerprints = Array.AsReadOnly(visualFingerprints.ToArray());
    }

    public Guid Id { get; }
    public Guid ScanRootId { get; }
    public string RelativePath { get; }
    public long SizeBytes { get; }
    public DateTimeOffset ModifiedUtc { get; }
    public ScanState ScanState { get; }
    public TimeSpan? Duration { get; }
    public int? Width { get; }
    public int? Height { get; }
    public string? VideoCodec { get; }
    public string? AudioCodec { get; }
    public bool? HasAudio { get; }
    public string? Sha256 { get; }
    public IReadOnlyList<VisualFingerprint> VisualFingerprints { get; }

    public static VideoFile Create(Guid id, Guid scanRootId, string relativePath, long sizeBytes, DateTimeOffset modifiedUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("File ID must not be empty.", nameof(id));
        if (scanRootId == Guid.Empty) throw new ArgumentException("Scan-root ID must not be empty.", nameof(scanRootId));
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        var identity = StableFileIdentity.Create(null, relativePath, sizeBytes, modifiedUtc);
        return new VideoFile(id, scanRootId, identity.RelativePath, sizeBytes, modifiedUtc, ScanState.Discovered,
            null, null, null, null, null, null, null, Array.Empty<VisualFingerprint>());
    }

    public VideoFile MarkDiscovered()
    {
        EnsureState(ScanState.NotScanned);
        return Copy(ScanState.Discovered, null, null, null, null, null, null, null, Array.Empty<VisualFingerprint>());
    }

    public VideoFile WithProbe(TimeSpan duration, int width, int height, string? videoCodec, string? audioCodec, bool hasAudio)
    {
        EnsureState(ScanState.Discovered);
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        return Copy(ScanState.Probed, duration, width, height, NormalizeOptional(videoCodec), NormalizeOptional(audioCodec), hasAudio, Sha256, VisualFingerprints);
    }

    public VideoFile WithHash(string sha256)
    {
        EnsureState(ScanState.Probed);
        ArgumentNullException.ThrowIfNull(sha256);
        var hash = sha256.Trim();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", nameof(sha256));
        return Copy(ScanState.Hashed, Duration, Width, Height, VideoCodec, AudioCodec, HasAudio, hash.ToLowerInvariant(), VisualFingerprints);
    }

    public VideoFile WithFingerprints(IEnumerable<VisualFingerprint> fingerprints)
    {
        EnsureState(ScanState.Hashed);
        ArgumentNullException.ThrowIfNull(fingerprints);
        var values = fingerprints.ToArray();
        if (values.Length == 0) throw new ArgumentException("At least one visual fingerprint is required.", nameof(fingerprints));
        if (values.Any(fingerprint => fingerprint is null)) throw new ArgumentException("Fingerprints cannot contain null.", nameof(fingerprints));
        if (Duration is not null && values.Any(fingerprint => fingerprint.Timestamp > Duration.Value))
            throw new ArgumentException("Fingerprint timestamps must not exceed the probed duration.", nameof(fingerprints));
        if (values.Select(fingerprint => fingerprint.Version).Distinct().Count() != 1)
            throw new ArgumentException("Fingerprints must use a single algorithm version.", nameof(fingerprints));

        return Copy(ScanState.Fingerprinted, Duration, Width, Height, VideoCodec, AudioCodec, HasAudio, Sha256, values);
    }

    public VideoFile MarkFailed() => TransitionTo(ScanState.Failed);

    public VideoFile MarkUnsupported() => TransitionTo(ScanState.Unsupported);

    public VideoFile ResetForRescan() => Copy(ScanState.NotScanned, null, null, null, null, null, null, null, Array.Empty<VisualFingerprint>());

    private VideoFile TransitionTo(ScanState state)
    {
        if (ScanState is ScanState.Failed or ScanState.Unsupported or ScanState.NotScanned)
            throw new InvalidOperationException("Only an active scan can be marked failed or unsupported.");
        return Copy(state, Duration, Width, Height, VideoCodec, AudioCodec, HasAudio, Sha256, VisualFingerprints);
    }

    private void EnsureState(ScanState expected)
    {
        if (ScanState != expected)
            throw new InvalidOperationException($"This transition requires {expected}, but the file is {ScanState}.");
    }

    private VideoFile Copy(ScanState state, TimeSpan? duration, int? width, int? height, string? videoCodec,
        string? audioCodec, bool? hasAudio, string? sha256, IEnumerable<VisualFingerprint> fingerprints) =>
        new(Id, ScanRootId, RelativePath, SizeBytes, ModifiedUtc, state, duration, width, height, videoCodec,
            audioCodec, hasAudio, sha256, fingerprints.ToArray());

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
