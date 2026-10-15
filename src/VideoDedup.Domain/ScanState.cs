namespace VideoDedup.Domain;

/// <summary>Where a video file currently sits in the scan pipeline.</summary>
public enum ScanState
{
    NotScanned,
    Discovered,
    Probed,
    Hashed,
    Fingerprinted,
    Failed,
    Unsupported,
}
