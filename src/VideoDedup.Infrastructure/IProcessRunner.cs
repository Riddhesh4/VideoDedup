namespace VideoDedup.Infrastructure;

public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, TimeSpan Timeout,
    int MaxOutputBytes = 1024 * 1024, long? MaxMemoryBytes = null, TimeSpan? MaxCpuTime = null);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed record BinaryProcessResult(int ExitCode, byte[] StandardOutput, string StandardError);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
    Task<BinaryProcessResult> RunBinaryAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
