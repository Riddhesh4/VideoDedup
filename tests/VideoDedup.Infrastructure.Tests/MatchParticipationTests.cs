using FluentAssertions;
using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class MatchParticipationTests
{
    private static readonly Guid RootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly FileRevision Revision = new(2048, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly MediaMetadata Metadata = new(8, 2, 320, 240, "ffv1", "pcm_s16le", true, 25,
        "matroska", null, "yuv420p", "bt709", 8);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MatchEvidenceSource Source(VisualProcessingRecord? visual, AudioProcessingRecord? audio,
        bool withAudioRepository = true)
    {
        var roots = Substitute.For<IScanRootRepository>();
        roots.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([ScanRoot.Create(RootId, "Videos", @"C:\Videos", ScanRootType.LocalFolder)]);
        var discovery = Substitute.For<IDiscoveryRepository>();
        discovery.GetFilesAsync(RootId, Arg.Any<CancellationToken>())
            .Returns([new IndexedFile(FileId, RootId, "clip.mkv", 2048, Revision.ModifiedUtc, ScanState.Fingerprinted)]);
        var media = Substitute.For<IMediaProcessingRepository>();
        media.GetAsync(FileId, Arg.Any<CancellationToken>())
            .Returns(new MediaProcessingRecord(FileId, Revision, Metadata, new string('a', 64)));
        var visuals = Substitute.For<IVisualProcessingRepository>();
        visuals.GetAsync(FileId, Arg.Any<CancellationToken>()).Returns(visual);
        var audios = withAudioRepository ? Substitute.For<IAudioProcessingRepository>() : null;
        audios?.GetAsync(FileId, Arg.Any<CancellationToken>()).Returns(audio);
        return new MatchEvidenceSource(discovery, roots, media, visuals, audios);
    }

    private static VisualProcessingRecord CurrentVisual(MediaFailure? failure = null)
    {
        var policy = new FrameSamplingPolicy();
        var frames = Enumerable.Range(1, 6)
            .Select(index => VisualFingerprint.Create(TimeSpan.FromSeconds(index),
                (ulong)index * 0x0101010101010101UL, 0.9m, VisualAlgorithm.Version))
            .ToList();
        return new VisualProcessingRecord(FileId, Revision, VisualAlgorithm.Name, VisualAlgorithm.Version,
            policy.Interval.Ticks, policy.MaxFrames, frames, failure);
    }

    private static AudioProcessingRecord Audio(MediaFailure? failure = null) =>
        AudioProcessingRecord.Create(FileId, Revision, AudioAlgorithm.Name, AudioAlgorithm.Version,
            TimeSpan.FromSeconds(8), new byte[64], hasAudio: true, failure);

    [Fact]
    public async Task A_terminal_audio_failure_does_not_hide_a_file_from_matching()
    {
        // Measured on a real scan: twelve identical copies produced zero matches because their audio stage failed
        // permanently and a failed stage used to exclude the file. Hash and visual evidence are enough to compare.
        var source = Source(CurrentVisual(), Audio(MediaFailure.InvalidData));

        var evidence = await source.GetEvidenceAsync(Token);

        evidence.Should().ContainSingle();
        evidence[0].IsReady.Should().BeTrue();
        evidence[0].Sha256.Should().Be(new string('a', 64));
        evidence[0].Audio.Should().BeNull("there is no audio evidence to compare, and that is a partial answer, not a wrong one");
        (await source.GetReadinessAsync(FileId, Token)).Should().Be(MatchReadiness.Ready);
    }

    [Fact]
    public async Task Audio_that_has_not_been_fingerprinted_yet_waits_for_its_turn()
    {
        var source = Source(CurrentVisual(), audio: null);

        (await source.GetEvidenceAsync(Token)).Should().BeEmpty();
        (await source.GetReadinessAsync(FileId, Token)).Should().Be(MatchReadiness.InProgress);
    }

    [Fact]
    public async Task A_stale_audio_record_is_treated_as_still_arriving()
    {
        var stale = AudioProcessingRecord.Create(FileId, new FileRevision(1, Revision.ModifiedUtc),
            AudioAlgorithm.Name, AudioAlgorithm.Version, TimeSpan.FromSeconds(8), new byte[16], hasAudio: true);
        var source = Source(CurrentVisual(), stale);

        (await source.GetEvidenceAsync(Token)).Should().BeEmpty();
        (await source.GetReadinessAsync(FileId, Token)).Should().Be(MatchReadiness.InProgress);
    }

    [Fact]
    public async Task A_file_whose_visual_stage_failed_can_never_be_compared()
    {
        var source = Source(CurrentVisual(MediaFailure.Unsupported), Audio());

        (await source.GetEvidenceAsync(Token)).Should().BeEmpty();
        (await source.GetReadinessAsync(FileId, Token)).Should().Be(MatchReadiness.Unprocessable);
    }

    [Fact]
    public async Task Wall_clock_fidelity_the_fixture_itself_participates_so_the_other_cases_are_meaningful()
    {
        var source = Source(CurrentVisual(), Audio());

        var evidence = await source.GetEvidenceAsync(Token);

        evidence.Should().ContainSingle();
        evidence[0].Audio.Should().NotBeNull("current audio evidence is used when it exists");
        evidence[0].VisualFrames.Should().NotBeEmpty();
        (await source.GetReadinessAsync(FileId, Token)).Should().Be(MatchReadiness.Ready);
    }

    [Fact]
    public async Task A_file_with_no_visual_record_yet_is_in_progress_not_unprocessable_and_unknown_files_are_not_indexed()
    {
        var source = Source(visual: null, Audio());

        (await source.GetReadinessAsync(FileId, Token)).Should().Be(MatchReadiness.InProgress);
        (await source.GetReadinessAsync(Guid.NewGuid(), Token)).Should().Be(MatchReadiness.NotIndexed);
    }

    [Fact]
    public async Task Visual_frames_from_an_older_revision_are_still_arriving()
    {
        var policy = new FrameSamplingPolicy();
        var stale = new VisualProcessingRecord(FileId, new FileRevision(1, Revision.ModifiedUtc), VisualAlgorithm.Name,
            VisualAlgorithm.Version, policy.Interval.Ticks, policy.MaxFrames, []);
        var source = Source(stale, Audio());

        (await source.GetEvidenceAsync(Token)).Should().BeEmpty();
        (await source.GetReadinessAsync(FileId, Token)).Should().Be(MatchReadiness.InProgress);
    }
}
