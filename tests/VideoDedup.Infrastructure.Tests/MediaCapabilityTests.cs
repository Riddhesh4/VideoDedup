using FluentAssertions;
using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class MediaCapabilityTests
{
    private const string Ffmpeg = @"C:\tools\media\ffmpeg.exe";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IProcessRunner Runner(int exitCode, string output)
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(exitCode, output, string.Empty));
        return runner;
    }

    [Fact]
    public void Parse_reads_the_accelerator_names_and_ignores_the_heading_and_noise()
    {
        var parsed = FfmpegMediaCapabilities.Parse(
            "Hardware acceleration methods:\nvulkan\ncuda\nd3d11va\n\nnot a name\n" + new string('x', 40) + "\n");

        parsed.Should().Equal("vulkan", "cuda", "d3d11va");
        FfmpegMediaCapabilities.Parse(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Select_prefers_a_deterministic_order_and_falls_back_to_software()
    {
        var selected = FfmpegMediaCapabilities.Select(["cuda", "d3d11va", "vulkan"]);
        selected.HardwareDecodeAvailable.Should().BeTrue();
        selected.Acceleration.Should().Be("d3d11va");
        selected.DisplayName.Should().Be("d3d11va");
        selected.Accelerators.Should().Equal("cuda", "d3d11va", "vulkan");

        var unsupported = FfmpegMediaCapabilities.Select(["vulkan", "opencl"]);
        unsupported.HardwareDecodeAvailable.Should().BeFalse();
        unsupported.Acceleration.Should().BeNull();
        unsupported.DisplayName.Should().Be(MediaCapabilities.SoftwareOnly);
        unsupported.Note.Should().Contain("software");

        FfmpegMediaCapabilities.Select([]).Note.Should().Contain("no hardware acceleration methods");
    }

    [Fact]
    public async Task Detection_asks_the_tool_once_and_caches_the_answer()
    {
        var runner = Runner(0, "Hardware acceleration methods:\ncuda\n");
        var probe = new FfmpegMediaCapabilities(runner, Ffmpeg);

        var first = await probe.DetectAsync(Token);
        var second = await probe.DetectAsync(Token);

        first.Acceleration.Should().Be("cuda");
        second.Should().BeSameAs(first);
        await runner.Received(1).RunAsync(
            Arg.Is<ProcessRequest>(request => request.Arguments.Contains("-hwaccels")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_or_missing_tool_never_blocks_decoding_it_simply_stays_in_software()
    {
        var failing = Runner(1, string.Empty);
        (await new FfmpegMediaCapabilities(failing, Ffmpeg).DetectAsync(Token)).HardwareDecodeAvailable.Should().BeFalse();

        var missing = Substitute.For<IProcessRunner>();
        missing.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProcessResult>>(_ => throw new MediaProcessingException(MediaFailure.ToolUnavailable));
        var capabilities = await new FfmpegMediaCapabilities(missing, Ffmpeg).DetectAsync(Token);
        capabilities.HardwareDecodeAvailable.Should().BeFalse();
        capabilities.Note.Should().Contain("probe failed");
    }

    [Fact]
    public async Task Cancellation_is_surfaced_rather_than_turned_into_a_software_answer()
    {
        var runner = Substitute.For<IProcessRunner>();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProcessResult>>(_ => throw new OperationCanceledException());

        await FluentActions.Awaiting(() => new FfmpegMediaCapabilities(runner, Ffmpeg).DetectAsync(source.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void An_accelerator_name_is_validated_before_it_reaches_the_argument_list()
    {
        var runner = Substitute.For<IProcessRunner>();
        var fingerprinter = new FfmpegVisualFingerprinter(runner, Ffmpeg, hardwareAcceleration: "d3d11va");
        fingerprinter.Should().NotBeNull();

        FluentActions.Invoking(() => new FfmpegVisualFingerprinter(runner, Ffmpeg, hardwareAcceleration: "d3d11va; rm -rf"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new FfmpegVisualFingerprinter(runner, Ffmpeg, hardwareAcceleration: new string('a', 40)))
            .Should().Throw<ArgumentException>();
    }
}
