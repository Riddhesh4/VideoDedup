using System.Globalization;
using System.Text.Json;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

public sealed class FfprobeMediaProbe(IProcessRunner runner, string executable, DecoderInventory? inventory = null) : IMediaProbe
{
    private readonly string _executable = Path.IsPathFullyQualified(executable)
        ? Path.GetFullPath(executable) : throw new ArgumentException("FFprobe must have an absolute configured path.", nameof(executable));

    public async Task<ProbeResult> ProbeAsync(string path, FileRevision expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        MediaFileAccess.Verify(path, expected);
        if (expected.SizeBytes == 0) throw new MediaProcessingException(MediaFailure.InvalidData);
        var result = await runner.RunAsync(new ProcessRequest(_executable,
            ["-v", "error", "-protocol_whitelist", "file", "-probesize", "5000000", "-analyzeduration", "5000000",
             "-show_streams", "-show_format", "-of", "json", "-i", path], TimeSpan.FromSeconds(30)), cancellationToken).ConfigureAwait(false);
        MediaFileAccess.Verify(path, expected);
        if (result.ExitCode != 0) throw new MediaProcessingException(MediaFailure.InvalidData);
        var metadata = Parse(result.StandardOutput);
        if (inventory is not null && !await inventory.SupportsAsync(metadata.VideoCodec!, cancellationToken).ConfigureAwait(false))
            throw new MediaProcessingException(MediaFailure.Unsupported);
        return new ProbeResult(expected, metadata);
    }

    public static MediaMetadata Parse(string json)
    {
        if (json.Length > 1024 * 1024) throw new MediaProcessingException(MediaFailure.OutputLimit);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() > 128)
                throw new MediaProcessingException(MediaFailure.InvalidData);
            JsonElement? video = null, audio = null;
            foreach (var stream in streams.EnumerateArray())
            {
                if (Text(stream, "codec_type") == "video") video ??= stream;
                if (Text(stream, "codec_type") == "audio") audio ??= stream;
            }
            if (video is null) throw new MediaProcessingException(MediaFailure.Unsupported);
            var v = video.Value;
            var codec = Text(v, "codec_name");
            if (codec is null or "unknown") throw new MediaProcessingException(MediaFailure.Unsupported);
            var format = doc.RootElement.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            double? rotation = null;
            if (v.TryGetProperty("side_data_list", out var side) && side.ValueKind == JsonValueKind.Array)
                foreach (var entry in side.EnumerateArray()) rotation ??= Number(entry, "rotation");
            if (rotation is null && v.TryGetProperty("tags", out var tags)) rotation = Number(tags, "rotate");
            var duration = Number(format, "duration") ?? Number(v, "duration");
            if (duration is < 0 or > 315576000) throw new MediaProcessingException(MediaFailure.InvalidData);
            var rate = Text(v, "avg_frame_rate");
            double? fps = null;
            if (rate?.Split('/') is [var numerator, var denominator] &&
                double.TryParse(numerator, CultureInfo.InvariantCulture, out var n) &&
                double.TryParse(denominator, CultureInfo.InvariantCulture, out var d) && d > 0 && double.IsFinite(n / d) && n / d > 0)
                fps = n / d;
            return new MediaMetadata(duration, streams.GetArrayLength(), PositiveInt(v, "width"), PositiveInt(v, "height"),
                codec, audio is null ? null : Text(audio.Value, "codec_name"), audio is not null, fps,
                Text(format, "format_name"), rotation, Text(v, "pix_fmt"), Text(v, "color_space"),
                PositiveInt(v, "bits_per_raw_sample") ?? PositiveInt(v, "bits_per_sample"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new MediaProcessingException(MediaFailure.InvalidData); }
    }

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
    }
    private static double? Number(JsonElement element, string name) =>
        double.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    private static int? PositiveInt(JsonElement element, string name) =>
        int.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
}
