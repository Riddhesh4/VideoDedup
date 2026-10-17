using System.Security.Cryptography;
using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class AudioFingerprintTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "VideoDedup.Audio", Guid.NewGuid().ToString("N"));
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VideoDedup.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Solution not found.");
        }
    }
    private static string Fpcalc => Path.Combine(Root, "tools/audio/chromaprint-fpcalc-1.6.2-windows-x86_64/fpcalc.exe");
    private static string Ffmpeg => Path.Combine(Root, "tools/media/ffmpeg.exe");
    private static string Probe => Path.Combine(Root, "tools/media/ffprobe.exe");
    private static ProcessRunner Runner() => MediaToolProcessRunnerFactory.Create(Probe, Ffmpeg, Fpcalc);
    private static ProcessRunner ProbeRunner() => new([Probe]);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Parses_valid_raw_word_fingerprint_and_rejects_malformed_or_unbounded_json()
    {
        var revision = new FileRevision(10, DateTimeOffset.UnixEpoch);
        var result = FpcalcAudioFingerprinter.ParseOutput(
            "{\"duration\":8.12,\"fingerprint\":[1,2,3,251,4294967295]}", revision);
        result.Duration.Should().Be(TimeSpan.FromSeconds(8.12));
        result.HasAudio.Should().BeTrue();
        result.Algorithm.Should().Be(AudioAlgorithm.Name);
        result.Version.Should().Be(AudioAlgorithm.Version);
        result.Fingerprint!.Data.Should().Equal(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 251, 0, 0, 0, 255, 255, 255, 255 });
        result.Fingerprint.Start.Should().Be(TimeSpan.Zero);
        result.Fingerprint.End.Should().Be(result.Duration);
        foreach (var invalid in new[] { "", "{", "{}", "[]", "{\"duration\":0,\"fingerprint\":[1]}",
                     "{\"duration\":NaN,\"fingerprint\":[1]}", "{\"duration\":1,\"fingerprint\":\"AQ\"}",
                     "{\"duration\":1,\"fingerprint\":[-1]}", "{\"duration\":1,\"fingerprint\":\"A\"}" })
            FluentActions.Invoking(() => FpcalcAudioFingerprinter.ParseOutput(invalid, revision)).Should().Throw<MediaProcessingException>();
        var oversized = "{\"duration\":1,\"fingerprint\":[" + string.Join(",", Enumerable.Repeat("1", FpcalcAudioFingerprinter.MaxFingerprintBytes / 4 + 1)) + "]}";
        FluentActions.Invoking(() => FpcalcAudioFingerprinter.ParseOutput(oversized, revision)).Should().Throw<MediaProcessingException>()
            .Which.Failure.Should().Be(MediaFailure.OutputLimit);
        FluentActions.Invoking(() => FpcalcAudioFingerprinter.ParseOutput(new string(' ', FpcalcAudioFingerprinter.MaxJsonBytes + 1), revision))
            .Should().Throw<MediaProcessingException>().Which.Failure.Should().Be(MediaFailure.OutputLimit);
    }

    [Fact]
    public async Task Uses_fixed_fpcalc_json_whole_file_arguments_and_normally_skips_known_no_audio_metadata()
    {
        var path = TempFile("-unicode ☃ & quote' video.mkv");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<ProcessRequest>();
            request.Arguments.Should().Equal("-json", "-length", "0", "-raw", path);
            request.Timeout.Should().Be(TimeSpan.FromMinutes(5));
            request.MaxOutputBytes.Should().Be(FpcalcAudioFingerprinter.MaxJsonBytes);
            return new ProcessResult(0, "{\"duration\":2,\"fingerprint\":[1,2,3]}", "");
        });
        var adapter = new FpcalcAudioFingerprinter(runner, Fpcalc);
        var withAudio = await adapter.FingerprintAsync(path, MediaFileAccess.Revision(path), Metadata(true), Token);
        withAudio.HasAudio.Should().BeTrue();
        var noAudio = await adapter.FingerprintAsync(path, MediaFileAccess.Revision(path), Metadata(false), Token);
        noAudio.HasAudio.Should().BeFalse();
        noAudio.Fingerprint.Should().BeNull();
        await runner.Received(1).RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_tool_nonzero_exit_timeout_and_changed_source_are_typed()
    {
        var path = TempFile();
        var missingPath = Path.Combine(_temp, "missing.exe");
        var missing = new FpcalcAudioFingerprinter(new ProcessRunner([missingPath]), missingPath);
        await FluentActions.Awaiting(() => missing.FingerprintAsync(path, MediaFileAccess.Revision(path), Metadata(true), Token))
            .Should().ThrowAsync<MediaProcessingException>().Where(exception => exception.Failure == MediaFailure.ToolUnavailable);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(new ProcessResult(2, "", "no usable audio"));
        await FluentActions.Awaiting(() => new FpcalcAudioFingerprinter(runner, Fpcalc)
                .FingerprintAsync(path, MediaFileAccess.Revision(path), Metadata(true), Token))
            .Should().ThrowAsync<MediaProcessingException>().Where(exception => exception.Failure == MediaFailure.InvalidData);
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            File.AppendAllText(path, "changed");
            return new ProcessResult(0, "{\"duration\":1,\"fingerprint\":[1,2,3]}", "");
        });
        await FluentActions.Awaiting(() => new FpcalcAudioFingerprinter(runner, Fpcalc)
                .FingerprintAsync(path, MediaFileAccess.Revision(path) with { SizeBytes = 1 }, Metadata(true), Token))
            .Should().ThrowAsync<MediaProcessingException>();
    }

    [Fact]
    public async Task Pinned_fpcalc_repeats_for_generated_pcm_and_reencoded_audio_is_compatible()
    {
        var wave = Path.Combine(Root, "TestFixtures/generated/audio-sine.wav");
        var encoded = Path.Combine(Root, "TestFixtures/generated/audio-sine-aac.m4a");
        var videoAudio = Path.Combine(Root, "TestFixtures/generated/sample.mkv");
        File.Exists(wave).Should().BeTrue("run scripts/prepare-media.py first");
        var version = await Runner().RunAsync(new ProcessRequest(Fpcalc, ["-version"], TimeSpan.FromSeconds(10), 4096), Token);
        version.ExitCode.Should().Be(0);
        version.StandardOutput.Trim().Should().StartWith("fpcalc version 1.6.2 ");
        var adapter = new FpcalcAudioFingerprinter(Runner(), Fpcalc);
        var original = await Fingerprint(wave, adapter);
        var repeated = await Fingerprint(wave, adapter);
        var reencoded = await Fingerprint(encoded, adapter);
        var embedded = await Fingerprint(videoAudio, adapter);
        original.Fingerprint!.Data.Should().Equal(repeated.Fingerprint!.Data);
        original.HasAudio.Should().BeTrue();
        reencoded.HasAudio.Should().BeTrue();
        (reencoded.Duration - original.Duration).Duration().Should().BeLessOrEqualTo(TimeSpan.FromSeconds(1),
            "the AAC re-encode should keep duration within a second of the original");
        reencoded.Fingerprint!.Data.Should().Equal(original.Fingerprint.Data,
            "the generated tonal fixture should remain fingerprint-compatible after AAC re-encoding");
        embedded.HasAudio.Should().BeTrue();
        embedded.Fingerprint.Should().NotBeNull();
    }

    [Fact]
    public async Task Real_video_without_audio_is_skipped_and_streamed_audio_video_selects_usable_audio()
    {
        var noAudio = Path.Combine(Root, "TestFixtures/generated/visual-original.mkv");
        var withAudio = Path.Combine(Root, "TestFixtures/generated/sample.mkv");
        var adapter = new FpcalcAudioFingerprinter(Runner(), Fpcalc);
        var noAudioProbe = await new FfprobeMediaProbe(ProbeRunner(), Path.Combine(Root, "tools/media/ffprobe.exe"))
            .ProbeAsync(noAudio, MediaFileAccess.Revision(noAudio), Token);
        (await adapter.FingerprintAsync(noAudio, MediaFileAccess.Revision(noAudio), noAudioProbe.Metadata, Token)).HasAudio.Should().BeFalse();
        var audioProbe = new MediaMetadata(8, 2, 64, 48, "ffv1", "pcm_s16le", true, 5, "matroska", null, "bgr0", null, 8);
        (await adapter.FingerprintAsync(withAudio, MediaFileAccess.Revision(withAudio), audioProbe, Token)).Fingerprint.Should().NotBeNull();
    }

    private async Task<AudioFingerprintResult> Fingerprint(string path, IAudioFingerprinter fingerprinter)
    {
        var revision = MediaFileAccess.Revision(path);
        var metadata = new MediaMetadata(8, 1, null, null, "audio-only", "pcm_s16le", true, null, null, null, null, null, 16);
        return await fingerprinter.FingerprintAsync(path, revision, metadata, Token);
    }
    private string TempFile(string name = "audio.mkv")
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }
    private static MediaMetadata Metadata(bool hasAudio) => new(1, hasAudio ? 2 : 1, 64, 48, "ffv1",
        hasAudio ? "pcm_s16le" : null, hasAudio, 5, "matroska", null, "yuv420p", null, 8);
    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
