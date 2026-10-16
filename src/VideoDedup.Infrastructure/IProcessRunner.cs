namespace VideoDedup.Infrastructure;

public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, TimeSpan Timeout,
    int MaxOutputBytes = 1024 * 1024);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
