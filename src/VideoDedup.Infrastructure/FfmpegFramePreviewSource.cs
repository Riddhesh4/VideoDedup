using System.Globalization;
using System.Text.RegularExpressions;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>
/// Decodes one bounded JPEG preview frame at a requested timestamp for the side-by-side review comparison.
/// The file revision is verified before and after decoding, output is capped, and any failure yields no preview
/// rather than an exception, because a missing preview must never block reviewing a group.
/// </summary>
public sealed class FfmpegFramePreviewSource(IProcessRunner runner, string executable) : IFramePreviewSource
{
    public const int MinWidth = 64;

    public const int MaxWidth = 1024;

    private readonly string _executable = Path.IsPathFullyQualified(executable)
        ? Path.GetFullPath(executable)
        : throw new ArgumentException("FFmpeg must have an absolute configured path.", nameof(executable));

    public async Task<FramePreview?> CaptureAsync(string path, FileRevision expected, TimeSpan timestamp, int width,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (width is < MinWidth or > MaxWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Preview width must be between {MinWidth} and {MaxWidth}.");
        }

        if (timestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        var full = Path.GetFullPath(path);
        MediaFileAccess.Verify(full, expected);
        BinaryProcessResult result;
        try
        {
            result = await runner.RunBinaryAsync(new ProcessRequest(_executable, Arguments(full, timestamp, width),
                TimeSpan.FromSeconds(20), FramePreview.MaxBytes, 512L * 1024 * 1024, TimeSpan.FromSeconds(10)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MediaProcessingException)
        {
            return null;
        }

        MediaFileAccess.Verify(full, expected);
        if (result.ExitCode != 0 || result.StandardOutput.Length is 0 or > FramePreview.MaxBytes)
        {
            return null;
        }

        var dimensions = ParseDimensions(result.StandardError);
        if (dimensions is not { } decoded)
        {
            return null;
        }

        return FramePreview.Create(result.StandardOutput, timestamp, decoded.Width, decoded.Height);
    }

    private static IReadOnlyList<string> Arguments(string path, TimeSpan timestamp, int width) =>
    [
        "-nostdin", "-hide_banner", "-v", "info", "-copyts", "-start_at_zero", "-max_alloc", "67108864",
        "-filter_threads", "1", "-protocol_whitelist", "file,pipe", "-probesize", "5000000",
        "-analyzeduration", "5000000", "-threads", "1", "-max_pixels", "8294400", "-hwaccel", "none",
        "-ss", timestamp.TotalSeconds.ToString("0.0000000", CultureInfo.InvariantCulture), "-i", path,
        "-map", "0:v:0", "-an", "-sn", "-dn",
        "-vf", $"scale={width}:-2:flags=area,format=yuvj420p,setsar=1,showinfo",
        "-frames:v", "1", "-fps_mode", "passthrough", "-threads", "1", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1",
    ];

    private static (int Width, int Height)? ParseDimensions(string diagnostics)
    {
        var match = Regex.Match(diagnostics, @"s:(\d{1,5})x(\d{1,5})", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var height) ||
            width is < 1 or > FramePreview.MaxDimension || height is < 1 or > FramePreview.MaxDimension)
        {
            return null;
        }

        return (width, height);
    }
}
