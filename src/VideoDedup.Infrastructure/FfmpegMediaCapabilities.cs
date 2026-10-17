using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>
/// What this machine can decode with. Hardware is a hint, never a requirement: when detection fails, is unsupported,
/// or names only accelerators we cannot use, the application stays on the deterministic software path.
/// </summary>
public sealed record MediaCapabilities(bool HardwareDecodeAvailable, string? Acceleration,
    IReadOnlyList<string> Accelerators, string Note)
{
    public const string SoftwareOnly = "software-only";

    public string DisplayName => Acceleration ?? SoftwareOnly;

    public static MediaCapabilities Software(string note) => new(false, null, [], note);
}

public interface IMediaCapabilityProbe
{
    /// <summary>Cached capability answer; detection runs at most once per process.</summary>
    Task<MediaCapabilities> DetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Detects usable FFmpeg hardware acceleration by asking the tool itself. The answer is only trusted as far as the
/// decoder list: actual decode failures still fall back to software inside the fingerprinter.
/// </summary>
public sealed class FfmpegMediaCapabilities : IMediaCapabilityProbe
{
    private static readonly string[] PreferredAccelerators = ["d3d11va", "dxva2", "cuda", "qsv"];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _runner;
    private readonly string _executable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapabilities? _cached;

    public FfmpegMediaCapabilities(IProcessRunner runner, string executable)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _executable = Path.IsPathFullyQualified(executable)
            ? Path.GetFullPath(executable)
            : throw new ArgumentException("FFmpeg must have an absolute configured path.", nameof(executable));
    }

    public async Task<MediaCapabilities> DetectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cached ??= await ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Parses <c>ffmpeg -hwaccels</c> output. The first line is a heading and is ignored.</summary>
    public static IReadOnlyList<string> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var lines = output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var accelerators = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var name = line.Trim();
            if (name.Length is 0 or > 32 || name.Contains(' ') || !name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                continue;
            }

            accelerators.Add(name);
        }

        return accelerators;
    }

    /// <summary>Deterministic preference order, so the same machine always picks the same accelerator.</summary>
    public static MediaCapabilities Select(IReadOnlyList<string> accelerators)
    {
        ArgumentNullException.ThrowIfNull(accelerators);
        foreach (var preferred in PreferredAccelerators)
        {
            if (accelerators.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            {
                return new MediaCapabilities(true, preferred,
                    accelerators.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                    $"Hardware decoding is available through '{preferred}' when the format allows it.");
            }
        }

        return new MediaCapabilities(false, null, accelerators.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            accelerators.Count == 0
                ? "This FFmpeg build reports no hardware acceleration methods; decoding in software."
                : $"No supported hardware accelerator was reported ({string.Join(", ", accelerators.OrderBy(name => name, StringComparer.Ordinal))}); decoding in software.");
    }

    private async Task<MediaCapabilities> ProbeAsync(CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessRequest(_executable, ["-hide_banner", "-hwaccels"], ProbeTimeout), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is MediaProcessingException or IOException or InvalidOperationException)
        {
            return MediaCapabilities.Software($"Hardware capability probe failed ({exception.GetType().Name}); decoding in software.");
        }

        return result.ExitCode == 0
            ? Select(Parse(result.StandardOutput))
            : MediaCapabilities.Software($"FFmpeg reported no hardware accelerators (exit {result.ExitCode}); decoding in software.");
    }
}
