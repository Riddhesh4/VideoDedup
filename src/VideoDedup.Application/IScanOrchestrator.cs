namespace VideoDedup.Application;

/// <summary>Placeholder application-layer contract; real scan orchestration arrives in Phase 2.</summary>
public interface IScanOrchestrator
{
    /// <summary>Number of scan roots currently configured.</summary>
    int ScanRootCount { get; }
}
