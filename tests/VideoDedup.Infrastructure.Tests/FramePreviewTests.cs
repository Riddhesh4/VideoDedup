using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class FramePreviewTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "VideoDedup.Preview", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private static string Sample => Path.Combine(Root, "TestFixtures/generated/sample.mkv");

    private string Copy { get { Directory.CreateDirectory(_temp); var path = Path.Combine(_temp, "clip.mkv"); File.Copy(Sample, path, true); return path; } }

    [Fact]
    public async Task Real_ffmpeg_produces_a_bounded_jpeg_preview_at_the_requested_timestamp()
    {
        var path = Copy;
        var source = new FfmpegFramePreviewSource(new ProcessRunner([Ffmpeg]), Ffmpeg);

        var preview = await source.CaptureAsync(path, MediaFileAccess.Revision(path), TimeSpan.FromSeconds(2), 320, Token);

        preview.Should().NotBeNull();
        preview!.ContentType.Should().Be("image/jpeg");
        preview.ImageBytes[0].Should().Be(0xFF, "a JPEG starts with the SOI marker");
        preview.ImageBytes[1].Should().Be(0xD8);
        preview.ImageBytes[^2].Should().Be(0xFF, "a JPEG ends with the EOI marker");
        preview.ImageBytes[^1].Should().Be(0xD9);
        preview.ImageBytes.Length.Should().BeLessThanOrEqualTo(FramePreview.MaxBytes);
        preview.Width.Should().Be(320);
        preview.Height.Should().BeGreaterThan(0);
        preview.Timestamp.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Preview_arguments_pin_pipe_output_bounded_frames_and_separate_arguments()
    {
        var path = Path.Combine(_temp, "-hostile & quote' unicode \u2603.mkv");
        Directory.CreateDirectory(_temp);
        await File.WriteAllBytesAsync(path, [1, 2, 3], Token);
        ProcessRequest? captured = null;
        var runner = Substitute.For<IProcessRunner>();
        runner.RunBinaryAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            captured = call.Arg<ProcessRequest>();
            return new BinaryProcessResult(0, [0xFF, 0xD8, 0xFF, 0xD9], "[Parsed_showinfo_1 @ fake] n: 0 s:320x180");
        });

        var preview = await new FfmpegFramePreviewSource(runner, Ffmpeg)
            .CaptureAsync(path, MediaFileAccess.Revision(path), TimeSpan.FromSeconds(1.5), 320, Token);

        var request = captured!;
        request.Arguments.Should().ContainInOrder("-frames:v", "1");
        request.Arguments.Should().ContainInOrder("-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1");
        request.Arguments.Should().ContainInOrder("-protocol_whitelist", "file,pipe");
        request.Arguments.Should().Contain("-hwaccel");
        request.Arguments.Last().Should().Be("pipe:1");
        request.Arguments.Should().Contain(path);
        request.Arguments.Should().ContainInOrder("-ss", "1.5000000");
        request.MaxOutputBytes.Should().Be(FramePreview.MaxBytes);
        preview!.Width.Should().Be(320);
        preview.Height.Should().Be(180);
    }

    [Fact]
    public async Task No_preview_is_returned_when_the_tool_fails_or_the_output_has_no_dimensions()
    {
        var path = Path.Combine(_temp, "clip.mkv");
        Directory.CreateDirectory(_temp);
        await File.WriteAllBytesAsync(path, [1, 2, 3], Token);
        var revision = MediaFileAccess.Revision(path);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunBinaryAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BinaryProcessResult(1, [], "boom"));
        var source = new FfmpegFramePreviewSource(runner, Ffmpeg);
        (await source.CaptureAsync(path, revision, TimeSpan.Zero, 320, Token)).Should().BeNull();

        runner.RunBinaryAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BinaryProcessResult(0, [0xFF, 0xD8, 0xFF, 0xD9], "no dimensions here"));
        (await source.CaptureAsync(path, revision, TimeSpan.Zero, 320, Token)).Should().BeNull();

        var oversized = new byte[FramePreview.MaxBytes + 1];
        runner.RunBinaryAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BinaryProcessResult(0, oversized, "s:320x180"));
        (await source.CaptureAsync(path, revision, TimeSpan.Zero, 320, Token)).Should().BeNull();
    }

    [Fact]
    public async Task Tool_failures_and_changed_sources_never_escape_as_raw_errors()
    {
        var path = Path.Combine(_temp, "clip.mkv");
        Directory.CreateDirectory(_temp);
        await File.WriteAllBytesAsync(path, [1, 2, 3], Token);
        var revision = MediaFileAccess.Revision(path);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunBinaryAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BinaryProcessResult>(new MediaProcessingException(MediaFailure.ToolUnavailable)));
        var source = new FfmpegFramePreviewSource(runner, Ffmpeg);

        (await source.CaptureAsync(path, revision, TimeSpan.Zero, 320, Token)).Should().BeNull();

        await FluentActions.Awaiting(() => source.CaptureAsync(path, revision, TimeSpan.Zero, 8, Token))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => source.CaptureAsync(path, revision, TimeSpan.FromSeconds(-1), 320, Token))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => source.CaptureAsync(path,
                new FileRevision(revision.SizeBytes + 1, revision.ModifiedUtc), TimeSpan.Zero, 320, Token))
            .Should().ThrowAsync<MediaProcessingException>()
            .Where(exception => exception.Failure == MediaFailure.Changed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
