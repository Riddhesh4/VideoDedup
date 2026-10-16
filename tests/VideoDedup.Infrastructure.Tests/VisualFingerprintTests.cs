using System.Numerics;
using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class VisualFingerprintTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "VideoDedup.Visual", Guid.NewGuid().ToString("N"));
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VideoDedup.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Solution not found.");
        }
    }
    private static string Ffmpeg => Path.Combine(Root, "tools/media/ffmpeg.exe");
    private static string Probe => Path.Combine(Root, "tools/media/ffprobe.exe");
    private static ProcessRunner Runner() => new([Ffmpeg, Probe]);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Perceptual_hash_is_repeatable_and_rejects_black_low_detail_and_static_frames()
    {
        var pixels = Pixels();
        var first = PerceptualHasher.Compute(pixels, TimeSpan.Zero);
        PerceptualHasher.Compute(pixels, TimeSpan.Zero).Should().Be(first);
        first.Disposition.Should().Be(FrameDisposition.Usable);
        first.Quality.Should().BeInRange(0.2m, 1m);
        PerceptualHasher.Compute(new byte[1024], TimeSpan.Zero).Disposition.Should().Be(FrameDisposition.Black);
        PerceptualHasher.Compute(Enumerable.Repeat((byte)100, 1024).ToArray(), TimeSpan.Zero).Disposition.Should().Be(FrameDisposition.LowDetail);
        PerceptualHasher.Compute(pixels, TimeSpan.FromSeconds(1), pixels).Disposition.Should().Be(FrameDisposition.NearStatic);
        FluentActions.Invoking(() => PerceptualHasher.Compute(new byte[1023], TimeSpan.Zero)).Should().Throw<ArgumentException>();
        var brighter = pixels.Select(p => (byte)Math.Min(255, p + 4)).ToArray();
        BitOperations.PopCount(first.PerceptualHash ^ PerceptualHasher.Compute(brighter, TimeSpan.Zero).PerceptualHash).Should().BeLessThanOrEqualTo(6);
    }

    [Fact]
    public async Task Hardware_failure_retries_software_with_bounded_binary_output_and_separate_arguments()
    {
        var path = TempFile("-unicode ☃ & quote' video.mkv");
        var calls = new List<ProcessRequest>();
        var runner = Substitute.For<IProcessRunner>();
        runner.RunBinaryAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<ProcessRequest>(); calls.Add(request);
            return new BinaryProcessResult(calls.Count == 1 ? 1 : 0, calls.Count == 1 ? [] : Pixels(), "[Parsed_showinfo_3 @ fake] n: 0 pts: 500 pts_time:0.5");
        });
        var result = await new FfmpegVisualFingerprinter(runner, Ffmpeg, new FrameSamplingPolicy(), tryHardware: true)
            .FingerprintAsync(path, MediaFileAccess.Revision(path), Metadata(), Token);
        result.Frames.Should().ContainSingle();
        calls.Should().HaveCount(2);
        calls[0].Arguments.Should().ContainInOrder("-hwaccel", "auto");
        calls[1].Arguments.Should().ContainInOrder("-hwaccel", "none");
        foreach (var request in calls)
        {
            request.Arguments.Should().ContainInOrder("-i", path);
            request.Arguments.Should().ContainInOrder("-protocol_whitelist", "file,pipe");
            request.Arguments.Should().ContainInOrder("-frames:v", "1");
            request.Arguments.Should().ContainInOrder("-max_pixels", "8294400");
            request.Arguments.Should().ContainInOrder("-max_alloc", "67108864");
            request.MaxOutputBytes.Should().BeLessThanOrEqualTo(66560);
            request.MaxMemoryBytes.Should().Be(512L * 1024 * 1024);
            request.MaxCpuTime.Should().Be(TimeSpan.FromSeconds(8));
            request.Timeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(1, 0, MediaFailure.InvalidData)]
    [InlineData(0, 0, MediaFailure.InvalidData)]
    [InlineData(0, 1023, MediaFailure.InvalidData)]
    [InlineData(0, 1025, MediaFailure.OutputLimit)]
    public async Task Decoder_failure_or_invalid_raw_frame_never_yields_fingerprints(int exit, int length, MediaFailure failure)
    {
        var path = TempFile();
        var runner = Substitute.For<IProcessRunner>();
        runner.RunBinaryAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(new BinaryProcessResult(exit, new byte[length], "untrusted"));
        await FluentActions.Awaiting(() => new FfmpegVisualFingerprinter(runner, Ffmpeg).FingerprintAsync(path, MediaFileAccess.Revision(path), Metadata(), Token))
            .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == failure);
    }

    [Fact]
    public async Task Invalid_metadata_and_cancellation_do_not_start_decoder_and_changes_invalidate_result()
    {
        var path = TempFile();
        var runner = Substitute.For<IProcessRunner>();
        var adapter = new FfmpegVisualFingerprinter(runner, Ffmpeg);
        foreach (var metadata in new[] { Metadata() with { DurationSeconds = null }, Metadata() with { DurationSeconds = double.NaN }, Metadata() with { Width = 100000 }, Metadata() with { DurationSeconds = 1e10 } })
            await FluentActions.Awaiting(() => adapter.FingerprintAsync(path, MediaFileAccess.Revision(path), metadata, Token)).Should().ThrowAsync<MediaProcessingException>();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await FluentActions.Awaiting(() => adapter.FingerprintAsync(path, MediaFileAccess.Revision(path), Metadata(), cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        await runner.DidNotReceiveWithAnyArgs().RunBinaryAsync(default!, Arg.Any<CancellationToken>());
        var expected = MediaFileAccess.Revision(path);
        runner.RunBinaryAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            File.AppendAllText(path, "changed");
            return new BinaryProcessResult(0, Pixels(), "[Parsed_showinfo_3 @ fake] n: 0 pts: 500 pts_time:0.5");
        });
        await FluentActions.Awaiting(() => adapter.FingerprintAsync(path, expected, Metadata(), Token)).Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.Changed);
    }

    [Fact]
    public async Task Real_resized_reencoded_content_is_candidate_but_unrelated_or_one_frame_is_not()
    {
        var original = await Extract("visual-original.mkv");
        var copy = await Extract("visual-resized.mp4");
        var unrelated = await Extract("visual-unrelated.mkv");
        (await Extract("visual-original.mkv")).Frames.Should().Equal(original.Frames);
        VisualCandidatePolicy.IsCandidate(original.Frames, copy.Frames).Should().BeTrue();
        VisualCandidatePolicy.IsCandidate(original.Frames, unrelated.Frames).Should().BeFalse();
        VisualCandidatePolicy.IsCandidate(original.Frames.Take(1).ToArray(), copy.Frames.Take(1).ToArray()).Should().BeFalse();
    }

    [Fact]
    public async Task Letterboxed_and_rotated_reencodes_remain_candidates()
    {
        var original = await Extract("visual-original.mkv");
        VisualCandidatePolicy.IsCandidate(original.Frames, (await Extract("visual-letterboxed.mp4")).Frames).Should().BeTrue();
        VisualCandidatePolicy.IsCandidate(original.Frames, (await Extract("visual-rotated.mp4")).Frames).Should().BeTrue();
    }

    [Theory]
    [InlineData("visual-10bit.mkv")]
    [InlineData("visual-vfr.mkv")]
    [InlineData("visual-rotated.mp4")]
    [InlineData("visual-short.mkv")]
    public async Task Real_pixel_formats_variable_fps_rotation_and_short_video_are_bounded(string name)
    {
        var result = await Extract(name);
        result.Frames.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(8);
        result.Frames.Should().OnlyContain(f => f.Version == VisualAlgorithm.Version && f.Quality >= 0 && f.Quality <= 1);
        result.Frames.Should().OnlyContain(f => f.Timestamp >= TimeSpan.Zero && f.Timestamp < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Real_static_and_black_content_cannot_supply_multiple_usable_frames()
    {
        (await Extract("visual-black.mkv")).Frames.Should().OnlyContain(f => f.Disposition == FrameDisposition.Black);
        (await Extract("visual-static.mkv")).Frames.Count(f => f.Disposition == FrameDisposition.Usable).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Black_bars_do_not_change_centered_content_hash_significantly()
    {
        var original = await Extract("visual-original.mkv");
        var letterboxed = await Extract("visual-letterboxed.mp4");
        original.Frames.Select(f => f.Timestamp).Should().Equal(letterboxed.Frames.Select(f => f.Timestamp));
        original.Frames.Zip(letterboxed.Frames).Should().OnlyContain(pair => BitOperations.PopCount(pair.First.PerceptualHash ^ pair.Second.PerceptualHash) <= 10);
        VisualCandidatePolicy.IsCandidate(original.Frames, letterboxed.Frames).Should().BeTrue();
    }

    [Fact]
    public async Task Binary_runner_preserves_all_bytes_and_enforces_output_limit_and_decoder_job_object_limits()
    {
        var request = new ProcessRequest(Ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=white:size=32x32", "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"], TimeSpan.FromSeconds(10));
        var result = await Runner().RunBinaryAsync(request, Token);
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().HaveCount(1024).And.OnlyContain(b => b == 255);
        await FluentActions.Awaiting(() => Runner().RunBinaryAsync(request with { MaxOutputBytes = 1023 }, Token)).Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.OutputLimit);
        var bounded = new ProcessRequest(Ffmpeg, ["-nostdin", "-v", "error", "-version"], TimeSpan.FromSeconds(5), 65536, 128L * 1024 * 1024, TimeSpan.FromSeconds(2));
        var child = await Runner().RunBinaryAsync(bounded, Token);
        child.ExitCode.Should().Be(0);
        if (OperatingSystem.IsWindows())
            await FluentActions.Awaiting(() => Runner().RunBinaryAsync(bounded with { MaxMemoryBytes = 64L * 1024 * 1024, Timeout = TimeSpan.FromMilliseconds(10), MaxCpuTime = TimeSpan.FromMilliseconds(5) }, Token))
                .Should().ThrowAsync<MediaProcessingException>().Where(e => e.Failure == MediaFailure.Timeout);
    }

    private static Task<VisualFingerprintResult> Extract(string name) => ExtractPath(Path.Combine(Root, "TestFixtures/generated", name));
    private static async Task<VisualFingerprintResult> ExtractPath(string path)
    {
        File.Exists(path).Should().BeTrue("run scripts/prepare-media.py first");
        var revision = MediaFileAccess.Revision(path);
        var metadata = await new FfprobeMediaProbe(Runner(), Probe).ProbeAsync(path, revision, Token);
        return await new FfmpegVisualFingerprinter(Runner(), Ffmpeg, new FrameSamplingPolicy(TimeSpan.FromSeconds(0.5), 8))
            .FingerprintAsync(path, revision, metadata.Metadata, Token);
    }
    private string TempFile(string name = "video.mkv")
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, name); File.WriteAllBytes(path, [1, 2, 3]); return path;
    }
    private static byte[] Pixels() => Enumerable.Range(0, 1024).Select(i => (byte)((i * 37 + i / 32 * 19) % 220 + 15)).ToArray();
    private static MediaMetadata Metadata() => new(1, 1, 64, 48, "ffv1", null, false, 1, "matroska", 0, "yuv420p", null, 8);
    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
