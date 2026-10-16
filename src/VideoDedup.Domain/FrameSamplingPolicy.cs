namespace VideoDedup.Domain;

public sealed class FrameSamplingPolicy
{
    public const int FrameLimit = 120;
    public static TimeSpan DurationLimit { get; } = TimeSpan.FromDays(7);
    public FrameSamplingPolicy(TimeSpan? interval = null, int maxFrames = 60)
    {
        Interval = interval ?? TimeSpan.FromSeconds(10);
        if (Interval < TimeSpan.FromSeconds(0.25) || Interval > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        if (maxFrames is < 1 or > FrameLimit) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        MaxFrames = maxFrames;
    }
    public TimeSpan Interval { get; }
    public int MaxFrames { get; }
    public IReadOnlyList<TimeSpan> Sample(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > DurationLimit) throw new ArgumentOutOfRangeException(nameof(duration));
        var demand = (duration.Ticks + Interval.Ticks - 1) / Interval.Ticks;
        var count = (int)Math.Min(demand, MaxFrames);
        var step = demand > MaxFrames ? duration.Ticks / count : Interval.Ticks;
        return Array.AsReadOnly(Enumerable.Range(0, count).Select(i =>
        {
            var start = i * step;
            var end = Math.Min(duration.Ticks, start + step);
            return TimeSpan.FromTicks(start + (end - start) / 2);
        }).ToArray());
    }
}

public enum FrameDisposition { Usable, Black, LowDetail, NearStatic }

public static class VisualAlgorithm
{
    // Normalization, DCT, quality thresholds and sampling defaults form this contract.
    public const string Name = "gray32-dct63";
    public static FingerprintVersion Version { get; } = FingerprintVersion.Create(1, 0);
}
