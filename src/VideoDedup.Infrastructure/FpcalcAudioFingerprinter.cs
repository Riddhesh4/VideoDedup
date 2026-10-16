using System.Text.Json;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

public sealed class FpcalcAudioFingerprinter(IProcessRunner runner, string executable) : IAudioFingerprinter
{
    public const int MaxJsonBytes = 1024 * 1024;
    public const int MaxFingerprintBytes = 256 * 1024;
    private readonly string _executable = Path.IsPathFullyQualified(executable)
        ? Path.GetFullPath(executable)
        : throw new ArgumentException("fpcalc must have an absolute configured path.", nameof(executable));

    public async Task<AudioFingerprintResult> FingerprintAsync(string path, FileRevision expected, MediaMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        MediaFileAccess.Verify(path, expected);
        if (expected.SizeBytes == 0) throw new MediaProcessingException(MediaFailure.InvalidData);
        if (!metadata.HasAudio) return new AudioFingerprintResult(expected, TimeSpan.Zero, null, AudioAlgorithm.Name, AudioAlgorithm.Version, false);
        var result = await runner.RunAsync(new ProcessRequest(_executable,
            ["-json", "-length", "0", path], TimeSpan.FromMinutes(5), MaxJsonBytes), cancellationToken).ConfigureAwait(false);
        MediaFileAccess.Verify(path, expected);
        if (result.ExitCode != 0) throw new MediaProcessingException(MediaFailure.InvalidData);
        return ParseOutput(result.StandardOutput, expected);
    }

    public static AudioFingerprintResult ParseOutput(string json, FileRevision expected)
    {
        if (json.Length > MaxJsonBytes) throw new MediaProcessingException(MediaFailure.OutputLimit);
        try
        {
            using var document = JsonDocument.Parse(json,
                new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("duration", out var durationElement) ||
                durationElement.ValueKind != JsonValueKind.Number || !durationElement.TryGetDouble(out var duration) ||
                !double.IsFinite(duration) || duration <= 0 || duration > FrameSamplingPolicy.DurationLimit.TotalSeconds ||
                !root.TryGetProperty("fingerprint", out var fingerprintElement) || fingerprintElement.ValueKind != JsonValueKind.String)
                throw new MediaProcessingException(MediaFailure.InvalidData);
            var encoded = fingerprintElement.GetString();
            if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxFingerprintBytes * 2)
                throw new MediaProcessingException(encoded is { Length: > MaxFingerprintBytes * 2 } ? MediaFailure.OutputLimit : MediaFailure.InvalidData);
            var fingerprint = DecodeBase64Url(encoded);
            if (fingerprint.Length is 0 or > MaxFingerprintBytes) throw new MediaProcessingException(MediaFailure.InvalidData);
            var boundedDuration = TimeSpan.FromSeconds(duration);
            if (boundedDuration <= TimeSpan.Zero || boundedDuration > FrameSamplingPolicy.DurationLimit)
                throw new MediaProcessingException(MediaFailure.InvalidData);
            var value = AudioFingerprint.Create(TimeSpan.Zero, boundedDuration, fingerprint, AudioAlgorithm.Version);
            return new AudioFingerprintResult(expected, boundedDuration, value, AudioAlgorithm.Name, AudioAlgorithm.Version, true);
        }
        catch (MediaProcessingException) { throw; }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or ArgumentException)
        { throw new MediaProcessingException(MediaFailure.InvalidData); }
    }

    private static byte[] DecodeBase64Url(string encoded)
    {
        if (encoded.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new FormatException("Invalid base64url fingerprint.");
        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch { 0 => "", 2 => "==", 3 => "=", _ => throw new FormatException("Invalid base64url length.") };
        return Convert.FromBase64String(base64);
    }
}

