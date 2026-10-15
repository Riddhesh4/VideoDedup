namespace VideoDedup.Infrastructure;

/// <summary>Placeholder infrastructure contract; real adapters arrive in Phase 2/4.</summary>
public interface IProcessRunner
{
    Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default);
}
