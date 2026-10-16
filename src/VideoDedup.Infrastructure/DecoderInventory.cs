using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

public sealed class DecoderInventory(IProcessRunner runner, string executable)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string>? _codecs;
    public async Task<bool> SupportsAsync(string codec, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_codecs is null)
            {
                var result = await runner.RunAsync(new ProcessRequest(executable, ["-v", "quiet", "-codecs"], TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0) throw new MediaProcessingException(MediaFailure.ToolFailed);
                _codecs = Parse(result.StandardOutput);
                if (_codecs.Count == 0) throw new MediaProcessingException(MediaFailure.ToolFailed);
            }
            return _codecs.Contains(codec);
        }
        finally { _gate.Release(); }
    }
    public static HashSet<string> Parse(string output)
    {
        var codecs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && columns[0].Length == 6 && columns[0][0] == 'D' && columns[0][2] == 'V') codecs.Add(columns[1]);
        }
        return codecs;
    }
}
