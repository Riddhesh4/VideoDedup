using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

public sealed class ProcessRunner(IEnumerable<string> allowedExecutables) : IProcessRunner
{
    private readonly HashSet<string> _allowed = new(allowedExecutables.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(request.Executable) || !_allowed.Contains(Path.GetFullPath(request.Executable)))
            throw new ArgumentException("Executable is not approved.", nameof(request));
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(10) || request.MaxOutputBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in request.Arguments) process.StartInfo.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        var budget = 0;
        async Task<string> ReadAsync(Stream stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (Interlocked.Add(ref budget, count) > request.MaxOutputBytes)
                {
                    deadline.Cancel();
                    throw new MediaProcessingException(MediaFailure.OutputLimit);
                }
                output.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        try
        {
            try { process.Start(); }
            catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
            { throw new MediaProcessingException(MediaFailure.ToolUnavailable); }
            process.StandardInput.Close();
            var stdout = ReadAsync(process.StandardOutput.BaseStream);
            var stderr = ReadAsync(process.StandardError.BaseStream);
            try
            {
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                // Observe both drain tasks before disposing redirected streams.
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or MediaProcessingException or IOException) { }
                if (budget > request.MaxOutputBytes) throw new MediaProcessingException(MediaFailure.OutputLimit);
                throw;
            }
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaProcessingException(MediaFailure.Timeout); }
    }
}
