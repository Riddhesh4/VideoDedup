using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Infrastructure.Tests;

/// <summary>
/// End-to-end partial-clip proof on real generated media: fpcalc fingerprints of the full fixture and a
/// stream-copy 4 s trim align inside a sliding window, and the visual confirmation at that offset — from
/// real FFmpeg frame fingerprints — makes the pair a classified PartialClip. Classification only, never an
/// automatic action.
/// </summary>
public sealed class PartialClipPipelineTests
{
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
    private static ProcessRunner Runner() => new([Fpcalc, Ffmpeg, Probe]);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Four_second_trim_of_the_primary_fixture_is_a_confirmed_partial_clip()
    {
        var container = Path.Combine(Root, "TestFixtures/generated/sample.mkv");
        var trim = Path.Combine(Root, "TestFixtures/generated/sample-trim.mkv");
        File.Exists(container).Should().BeTrue("run python scripts/prepare-media.py first");
        File.Exists(trim).Should().BeTrue("run python scripts/prepare-media.py first");

        var runner = Runner();
        var probe = new FfprobeMediaProbe(new ProcessRunner([Probe]), Probe);
        var containerMetadata = (await probe.ProbeAsync(container, MediaFileAccess.Revision(container), Token)).Metadata;
        var trimMetadata = (await probe.ProbeAsync(trim, MediaFileAccess.Revision(trim), Token)).Metadata;
        var audioFingerprinter = new FpcalcAudioFingerprinter(runner, Fpcalc);
        // Dense one-second sampling: the default 10 s interval would sample these short fixtures once each.
        var visualFingerprinter = new FfmpegVisualFingerprinter(runner, Ffmpeg, sampling: new FrameSamplingPolicy(TimeSpan.FromSeconds(1)));
        var containerRevision = MediaFileAccess.Revision(container);
        var trimRevision = MediaFileAccess.Revision(trim);
        var containerAudio = (await audioFingerprinter.FingerprintAsync(container, containerRevision, containerMetadata, Token)).Fingerprint;
        var trimAudio = (await audioFingerprinter.FingerprintAsync(trim, trimRevision, trimMetadata, Token)).Fingerprint;
        var containerVisual = (await visualFingerprinter.FingerprintAsync(container, containerRevision, containerMetadata, Token)).Frames;
        var trimVisual = (await visualFingerprinter.FingerprintAsync(trim, trimRevision, trimMetadata, Token)).Frames;

        containerAudio.Should().NotBeNull();
        trimAudio.Should().NotBeNull();
        var alignment = PartialAudioPolicy.BestWindowAlignment(trimAudio!, containerAudio!);
        alignment.Should().NotBeNull("the 4 s stream-copy trim is an excerpt of the 8 s container");
        alignment!.Similarity.Should().BeGreaterThanOrEqualTo(MatchPolicyOptions.Default.PartialClipThreshold);
        alignment.Offset.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));

        var confirmation = VideoSequenceSimilarityPolicy.SimilarityAtOffset(trimVisual, containerVisual, alignment.Offset);
        confirmation.Should().NotBeNull();
        confirmation!.Value.Should().BeGreaterThanOrEqualTo(MatchPolicyOptions.Default.ConfirmationThreshold);

        var candidate = MatchCandidate.Create(Guid.NewGuid(), Guid.NewGuid(),
            visualVersion: VisualAlgorithm.Version, audioVersion: AudioAlgorithm.Version,
            visualSimilarityAtOffset: confirmation, partialAudioSimilarity: alignment.Similarity,
            partialAudioOffsetSeconds: (decimal)alignment.Offset.TotalSeconds);
        var decision = new DuplicateMatchPolicy(MatchPolicyOptions.Default).Evaluate(candidate);
        decision.MatchType.Should().Be(MatchType.PartialClip);
        decision.AudioOffsetSeconds.Should().BeLessThanOrEqualTo(1m);
        decision.Reasons.Should().Contain(reason => reason.Contains("offset", StringComparison.OrdinalIgnoreCase));
    }
}
