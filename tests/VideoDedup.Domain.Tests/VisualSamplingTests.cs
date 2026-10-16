using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public sealed class VisualSamplingTests
{
    [Fact]
    public void Sampling_uses_midpoints_and_spreads_capped_demand_across_the_video()
    {
        var policy = new FrameSamplingPolicy(TimeSpan.FromSeconds(10), 3);
        policy.Sample(TimeSpan.FromSeconds(1)).Should().Equal(TimeSpan.FromSeconds(0.5));
        policy.Sample(TimeSpan.FromSeconds(30)).Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(25));
        var longVideo = policy.Sample(TimeSpan.FromDays(7));
        longVideo.Should().HaveCount(3).And.BeInAscendingOrder();
        longVideo[^1].Should().BeLessThan(TimeSpan.FromDays(7));
        policy.Sample(TimeSpan.FromDays(7)).Should().Equal(longVideo);
        new FrameSamplingPolicy(TimeSpan.FromSeconds(0.25), 120).Sample(TimeSpan.FromDays(7)).Should().HaveCount(120);
        policy.Sample(TimeSpan.FromTicks(1)).Should().Equal(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.1, 1)]
    [InlineData(3601, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 121)]
    public void Invalid_sampling_configuration_is_rejected(double seconds, int count) =>
        FluentActions.Invoking(() => new FrameSamplingPolicy(TimeSpan.FromSeconds(seconds), count)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Unknown_zero_negative_and_excessive_duration_are_not_unbounded_work()
    {
        var policy = new FrameSamplingPolicy();
        foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.MaxValue })
            FluentActions.Invoking(() => policy.Sample(duration)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Candidate_requires_multiple_distinct_usable_version_compatible_frames()
    {
        var frames = new[] { Frame(0, 0xFF), Frame(1, 0xFF00), Frame(2, 0xFF0000) };
        VisualCandidatePolicy.IsCandidate(frames, frames).Should().BeTrue();
        VisualCandidatePolicy.IsCandidate(frames.Take(1).ToArray(), frames.Take(1).ToArray()).Should().BeFalse();
        VisualCandidatePolicy.IsCandidate([], []).Should().BeFalse();
        VisualCandidatePolicy.IsCandidate(frames, frames.Select(f => Frame(f.Timestamp.TotalSeconds, ~f.PerceptualHash)).ToArray()).Should().BeFalse();
        VisualCandidatePolicy.IsCandidate(frames, frames.Select(f => Frame(f.Timestamp.TotalSeconds, f.PerceptualHash, version: 2)).ToArray()).Should().BeFalse();
        VisualCandidatePolicy.IsCandidate(frames, frames.Select(f => Frame(f.Timestamp.TotalSeconds, f.PerceptualHash, quality: 0.1m)).ToArray()).Should().BeFalse();
        VisualCandidatePolicy.IsCandidate(frames, frames.Select(f => Frame(f.Timestamp.TotalSeconds, f.PerceptualHash, disposition: FrameDisposition.NearStatic)).ToArray()).Should().BeFalse();
        var repeated = Enumerable.Range(0, 5).Select(i => Frame(i, 0xFF)).ToArray();
        VisualCandidatePolicy.IsCandidate(repeated, repeated).Should().BeFalse();
        VisualCandidatePolicy.IsCandidate(frames, frames.Select(f => Frame(f.Timestamp.TotalSeconds + 1, f.PerceptualHash)).ToArray()).Should().BeFalse();
        FluentActions.Invoking(() => Frame(0, 1, disposition: (FrameDisposition)99)).Should().Throw<ArgumentOutOfRangeException>();
    }

    private static VisualFingerprint Frame(double seconds, ulong hash, int version = 1, decimal quality = 1,
        FrameDisposition disposition = FrameDisposition.Usable) =>
        VisualFingerprint.Create(TimeSpan.FromSeconds(seconds), hash, quality, FingerprintVersion.Create(version, 0), disposition);
}
