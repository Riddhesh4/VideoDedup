using System.Globalization;
using System.Text.RegularExpressions;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

public sealed class FfmpegVisualFingerprinter(IProcessRunner runner, string executable, FrameSamplingPolicy? sampling = null,
    bool tryHardware = false, string? hardwareAcceleration = null) : IVisualFingerprinter
{
    private readonly string _executable = Path.IsPathFullyQualified(executable) ? Path.GetFullPath(executable) : throw new ArgumentException("FFmpeg must have an absolute configured path.", nameof(executable));
    private readonly FrameSamplingPolicy _sampling = sampling ?? new();

    // The accelerator is chosen by detection, not by the caller's hopes, and is validated as a bare identifier so it
    // can only ever be an FFmpeg-known method name.
    private readonly string _hardware = ResolveHardware(hardwareAcceleration);

    private static string ResolveHardware(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return "auto";
        }

        var name = requested.Trim();
        if (name.Length > 32 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            throw new ArgumentException("A hardware accelerator name must be a short identifier.", nameof(requested));
        }

        return name;
    }
    public async Task<VisualFingerprintResult> FingerprintAsync(string path, FileRevision expected, MediaMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        MediaFileAccess.Verify(path, expected);
        if (expected.SizeBytes == 0 || metadata.DurationSeconds is not double duration || !double.IsFinite(duration) || duration <= 0 || duration > FrameSamplingPolicy.DurationLimit.TotalSeconds ||
            metadata.Width is not > 0 or > 8192 || metadata.Height is not > 0 or > 8192 || (long)metadata.Width * metadata.Height > 8294400)
            throw new MediaProcessingException(MediaFailure.Unsupported);
        TimeSpan boundedDuration;
        try { boundedDuration = TimeSpan.FromSeconds(duration); }
        catch (OverflowException) { throw new MediaProcessingException(MediaFailure.Unsupported); }
        if (boundedDuration <= TimeSpan.Zero || boundedDuration > FrameSamplingPolicy.DurationLimit)
            throw new MediaProcessingException(MediaFailure.Unsupported);
        var timestamps = _sampling.Sample(boundedDuration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var frames = new List<VisualFingerprint>(timestamps.Count);
        byte[]? previous = null;
        try
        {
            foreach (var timestamp in timestamps)
            {
                deadline.Token.ThrowIfCancellationRequested();
                BinaryProcessResult? decoded = null;
                // Hardware is opt-in and capability-detected. High-depth inputs always take the deterministic
                // software path, and any hardware failure below falls back to software for the same frame.
                if (tryHardware && metadata.BitDepth is not > 8)
                {
                    try { decoded = await DecodeAsync(path, timestamp, _hardware, deadline.Token).ConfigureAwait(false); }
                    catch (MediaProcessingException exception) when (exception.Failure is MediaFailure.ToolFailed or MediaFailure.Timeout) { }
                }
                if (decoded is null || decoded.ExitCode != 0 || decoded.StandardOutput.Length == 0)
                    decoded = await DecodeAsync(path, timestamp, "none", deadline.Token).ConfigureAwait(false);
                // Short/VFR streams may end before a midpoint. Bounded retry at the clip's first frame.
                if (decoded.ExitCode == 0 && decoded.StandardOutput.Length == 0)
                    decoded = await DecodeAsync(path, TimeSpan.Zero, "none", deadline.Token).ConfigureAwait(false);
                MediaFileAccess.Verify(path, expected);
                if (decoded.ExitCode != 0) throw new MediaProcessingException(MediaFailure.InvalidData);
                if (decoded.StandardOutput.Length > PerceptualHasher.PixelCount) throw new MediaProcessingException(MediaFailure.OutputLimit);
                if (decoded.StandardOutput.Length != PerceptualHasher.PixelCount) throw new MediaProcessingException(MediaFailure.InvalidData);
                var sampledAt = ParseTimestamp(decoded.StandardError, duration);
                if (frames.Count == 0 || sampledAt > frames[^1].Timestamp)
                    frames.Add(PerceptualHasher.Compute(decoded.StandardOutput, sampledAt, previous));
                previous = decoded.StandardOutput;
            }
            MediaFileAccess.Verify(path, expected);
            return new VisualFingerprintResult(expected, frames.AsReadOnly());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaProcessingException(MediaFailure.Timeout); }
    }
    private static TimeSpan ParseTimestamp(string diagnostics, double duration)
    {
        var match = Regex.Match(diagnostics, @"\[Parsed_showinfo_\d+ @ [^\]\r\n]+\] n:\s*0\s+pts:\s*-?\d+\s+pts_time:([-+\d.eE]+)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > duration)
            throw new MediaProcessingException(MediaFailure.InvalidData);
        return TimeSpan.FromSeconds(seconds);
    }
    private Task<BinaryProcessResult> DecodeAsync(string path, TimeSpan timestamp, string hardware, CancellationToken cancellationToken) =>
        runner.RunBinaryAsync(new ProcessRequest(_executable,
            ["-nostdin", "-hide_banner", "-v", "info", "-copyts", "-start_at_zero", "-max_alloc", "67108864", "-filter_threads", "1",
             "-protocol_whitelist", "file,pipe", "-probesize", "5000000", "-analyzeduration", "5000000",
             "-threads", "1", "-max_pixels", "8294400", "-hwaccel", hardware, "-autorotate",
             "-ss", timestamp.TotalSeconds.ToString("0.0000000", CultureInfo.InvariantCulture), "-i", path,
             "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", "scale=32:32:force_original_aspect_ratio=decrease:flags=area,pad=32:32:(ow-iw)/2:(oh-ih)/2:color=black,format=gray,setsar=1,showinfo",
             "-frames:v", "1", "-fps_mode", "passthrough", "-threads", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"],
             TimeSpan.FromSeconds(10), 66560, 512L * 1024 * 1024, TimeSpan.FromSeconds(8)), cancellationToken);
}
