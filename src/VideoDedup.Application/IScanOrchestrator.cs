using VideoDedup.Domain;

namespace VideoDedup.Application;

/// <summary>Coordinates a cancellable discovery scan for one configured root.</summary>
public interface IScanOrchestrator
{
    Task<DiscoveryResult> ScanAsync(ScanRoot root, CancellationToken cancellationToken = default);
}
