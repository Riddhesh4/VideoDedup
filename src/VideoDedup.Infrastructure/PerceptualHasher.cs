using System.Numerics;
using VideoDedup.Domain;

namespace VideoDedup.Infrastructure;

public static class PerceptualHasher
{
    public const int Side = 32;
    public const int PixelCount = Side * Side;
    private static readonly double[,] Cosines = BuildCosines();

    public static VisualFingerprint Compute(ReadOnlySpan<byte> pixels, TimeSpan timestamp, ReadOnlySpan<byte> previous = default)
    {
        if (pixels.Length != PixelCount || (!previous.IsEmpty && previous.Length != PixelCount)) throw new ArgumentException("Expected a 32x32 grayscale frame.");
        double sum = 0, squares = 0, edges = 0, difference = 0;
        var minimum = byte.MaxValue; var maximum = byte.MinValue;
        for (var i = 0; i < PixelCount; i++)
        {
            sum += pixels[i]; squares += pixels[i] * pixels[i];
            minimum = Math.Min(minimum, pixels[i]); maximum = Math.Max(maximum, pixels[i]);
            if (i % Side != 0) edges += Math.Abs(pixels[i] - pixels[i - 1]);
            if (i >= Side) edges += Math.Abs(pixels[i] - pixels[i - Side]);
            if (!previous.IsEmpty) difference += Math.Abs(pixels[i] - previous[i]);
        }
        var mean = sum / PixelCount;
        var deviation = Math.Sqrt(Math.Max(0, squares / PixelCount - mean * mean));
        var quality = (decimal)Math.Clamp(Math.Min((maximum - minimum) / 48d, edges / (2 * Side * (Side - 1)) / 12), 0, 1);
        var hash = Hash(pixels);
        var disposition = maximum < 8 ? FrameDisposition.Black :
            maximum - minimum < 8 ? FrameDisposition.LowDetail :
            !previous.IsEmpty && difference / PixelCount <= 1 ? FrameDisposition.NearStatic : FrameDisposition.Usable;
        return VisualFingerprint.Create(timestamp, hash, quality, VisualAlgorithm.Version, disposition);
    }

    private static ulong Hash(ReadOnlySpan<byte> pixels)
    {
        // Separable 2-D DCT: 32x32 -> 8x8; omit the DC term to tolerate brightness shifts.
        Span<double> horizontal = stackalloc double[Side * 8];
        for (var y = 0; y < Side; y++)
            for (var u = 0; u < 8; u++)
            {
                double sum = 0;
                for (var x = 0; x < Side; x++) sum += pixels[y * Side + x] * Cosines[u, x];
                horizontal[y * 8 + u] = sum;
            }
        Span<double> coefficients = stackalloc double[63];
        var index = 0;
        for (var v = 0; v < 8; v++)
            for (var u = 0; u < 8; u++)
            {
                if (u == 0 && v == 0) continue;
                double sum = 0;
                for (var y = 0; y < Side; y++) sum += horizontal[y * 8 + u] * Cosines[v, y];
                // Quantization prevents floating-point roundoff in otherwise-zero DCT terms changing bits.
                coefficients[index++] = Math.Round(sum * (u == 0 ? 1 / Math.Sqrt(2) : 1) * (v == 0 ? 1 / Math.Sqrt(2) : 1), 6);
            }
        Span<double> sorted = stackalloc double[63]; coefficients.CopyTo(sorted); sorted.Sort();
        var median = sorted[31];
        ulong hash = 0;
        for (var i = 0; i < 63; i++) if (coefficients[i] > median) hash |= 1UL << i;
        return hash;
    }
    private static double[,] BuildCosines()
    {
        var result = new double[8, Side];
        for (var u = 0; u < 8; u++)
            for (var x = 0; x < Side; x++) result[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Side));
        return result;
    }
}
