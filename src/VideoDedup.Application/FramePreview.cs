namespace VideoDedup.Application;

/// <summary>One decoded preview frame. Bytes are a JPEG image; the UI decodes them and never re-parses media.</summary>
public sealed record FramePreview
{
    private FramePreview(byte[] imageBytes, TimeSpan timestamp, int width, int height)
    {
        _imageBytes = imageBytes;
        Timestamp = timestamp;
        Width = width;
        Height = height;
    }

    private readonly byte[] _imageBytes;

    /// <summary>Defensive copy so a preview cannot be mutated by its consumer.</summary>
    public byte[] ImageBytes => (byte[])_imageBytes.Clone();

    public string ContentType => "image/jpeg";

    public TimeSpan Timestamp { get; }

    public int Width { get; }

    public int Height { get; }

    public static FramePreview Create(ReadOnlySpan<byte> imageBytes, TimeSpan timestamp, int width, int height)
    {
        if (imageBytes.Length is 0 or > MaxBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(imageBytes), $"A preview must be between 1 and {MaxBytes} bytes.");
        }

        if (timestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Preview dimensions are out of range.");
        }

        return new FramePreview(imageBytes.ToArray(), timestamp, width, height);
    }

    public const int MaxBytes = 2 * 1024 * 1024;

    public const int MaxDimension = 2048;
}

/// <summary>
/// Read-only preview port. Implementations verify the file revision before and after decoding and return null when
/// no frame can be produced, so a broken preview never fails the review flow.
/// </summary>
public interface IFramePreviewSource
{
    Task<FramePreview?> CaptureAsync(string path, FileRevision expected, TimeSpan timestamp, int width, CancellationToken cancellationToken);
}
