namespace VideoDedup.Workers;

/// <summary>Placeholder worker contract; BackgroundServices arrive in Phase 2/3.</summary>
public interface IWorkerHeartbeat
{
    /// <summary>True once at least one hosted worker has registered its first heartbeat.</summary>
    bool IsAlive { get; }
}
