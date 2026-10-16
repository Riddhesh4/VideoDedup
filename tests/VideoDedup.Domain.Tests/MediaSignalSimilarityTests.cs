using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public sealed class MediaSignalSimilarityTests
{
    private static readonly FingerprintVersion Version = VisualAlgorithm.Version;
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static VisualFingerprint Frame(int index, ulong hash, byte quality = 80) =>
        VisualFingerprint.Create(TimeSpan.FromSeconds(index), hash, quality / 100m, Version);

    private static IReadOnlyList<VisualFingerprint> Sequence(Func<int, ulong> hashFor, int count = 6)
    {
        var frames = new List<VisualFingerprint>();
        for (var index = 0; index < count; index++)
            frames.Add(Frame(index, hashFor(index)));
        return frames;
    }

    private static AudioFingerprint Audio(TimeSpan duration, IReadOnlyList<uint> words) =>
        AudioFingerprint.Create(TimeSpan.Zero, duration, Encode(words), AudioAlgorithm.Version);

    private static byte[] Encode(IReadOnlyList<uint> words) =>
        words.SelectMany(BitConverter.GetBytes).ToArray();

    private static List<uint> Words(int count, uint seed) =>
        Enumerable.Range(0, count).Select(i => seed + (uint)i * 0x9E3779B1u).ToList();

    [Fact]
    public void Identical_visual_sequences_score_one()
    {
        var first = Sequence(i => (ulong)(i + 1) * 0x0101010101010101UL);
        VideoSequenceSimilarityPolicy.Similarity(first, Sequence(i => (ulong)(i + 1) * 0x0101010101010101UL)).Should().Be(1m);
    }

    [Fact]
    public void Perceptually_close_reencode_scores_above_the_match_threshold()
    {
        // Low two-bit payload differences within the frame Hamming tolerance of a re-encode.
        var close = Sequence(i => (ulong)(i + 1) * 0x0101010101010101UL ^ 0x0000000000000003UL);
        var original = Sequence(i => (ulong)(i + 1) * 0x0101010101010101UL);
        VideoSequenceSimilarityPolicy.Similarity(original, close).Should().Be(1m);
    }

    [Fact]
    public void Unrelated_visual_content_scores_zero_and_insufficient_sequences_return_no_evidence()
    {
        var original = Sequence(i => (ulong)(i + 1) * 0x0101010101010101UL);
        var unrelated = Sequence(i => (ulong)(i + 1) * 0x5555555555555555UL);
        VideoSequenceSimilarityPolicy.Similarity(original, unrelated).Should().Be(0m);

        var twoFrames = Sequence(i => (ulong)(i + 1) * 0x0101010101010101UL, count: 2);
        VideoSequenceSimilarityPolicy.Similarity(original, twoFrames).Should().BeNull();

        // Identical repeated frames (black screens) cannot become evidence.
        VideoSequenceSimilarityPolicy.Similarity(original, Sequence(_ => 0UL)).Should().BeNull();
    }

    [Fact]
    public void Word_aligned_audio_similarity_uses_hamming_distance_and_is_symmetric()
    {
        var audioA = Audio(TimeSpan.FromSeconds(64), Words(32, 1u));
        var audioSame = Audio(TimeSpan.FromSeconds(64), Words(32, 1u));
        AudioSimilarityPolicy.Similarity(audioA, audioSame).Should().Be(1m);

        // Exactly two words exceed the bit tolerance: 30/32 matched -> 0.9375, symmetric.
        var noisy = Words(32, 1u).Index().Select(pair => pair.Item ^ (pair.Index is 3 or 17 ? 0x0000003Fu : 0u)).ToList();
        AudioSimilarityPolicy.Similarity(audioA, Audio(TimeSpan.FromSeconds(64), noisy)).Should().Be(0.9375m);
    }

    [Fact]
    public void Mismatched_versions_degenerate_payloads_and_silence_are_not_evidence()
    {
        var words = Words(16, 7u);
        var original = Audio(TimeSpan.FromSeconds(32), words);
        var staleVersion = AudioFingerprint.Create(TimeSpan.Zero, TimeSpan.FromSeconds(32), Encode(words), FingerprintVersion.Create(3, 0));
        AudioSimilarityPolicy.Similarity(original, staleVersion).Should().BeNull();

        var oneWord = Audio(TimeSpan.FromSeconds(4), Words(1, 3u));
        AudioSimilarityPolicy.Similarity(original, oneWord).Should().BeNull();

        var silence = Audio(TimeSpan.FromSeconds(32), Enumerable.Repeat(0u, 32).ToList());
        AudioSimilarityPolicy.Similarity(silence, silence).Should().BeNull("identical silent tracks must not become candidates");
    }

    [Fact]
    public void Whole_file_excerpt_is_detected_as_a_partial_clip_with_offset()
    {
        var full = Audio(TimeSpan.FromSeconds(32), Words(32, 0xBADC0DE0u));
        var excerpt = Audio(TimeSpan.FromSeconds(8), Words(32, 0xBADC0DE0u).Skip(8).Take(8).ToList());
        var alignment = PartialAudioPolicy.BestWindowAlignment(excerpt, full);
        alignment.Should().NotBeNull();
        alignment!.Similarity.Should().Be(1m);
        alignment.Offset.Should().BeGreaterThan(TimeSpan.FromSeconds(7));
        alignment.Offset.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(8.5));
        alignment.WindowDuration.Should().Be(TimeSpan.FromSeconds(8));
        alignment.Similarity.Should().Be(PartialAudioPolicy.BestWindowAlignment(excerpt, full)!.Similarity);
    }

    [Fact]
    public void Unrelated_or_too_short_windows_do_not_produce_partial_evidence()
    {
        var full = Audio(TimeSpan.FromSeconds(32), Words(32, 0xBADC0DE0u));
        var unrelated = Audio(TimeSpan.FromSeconds(8), Words(8, 0x12345678u));
        PartialAudioPolicy.BestWindowAlignment(unrelated, full).Should().BeNull();

        var tinyWindow = Audio(TimeSpan.FromSeconds(2), Words(4, 0xBADC0DE0u));
        PartialAudioPolicy.BestWindowAlignment(tinyWindow, full).Should().BeNull();

        PartialAudioPolicy.BestWindowAlignment(full, full).Should().BeNull();
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Candidate_rejects_similarity_outside_zero_to_one(double score) =>
        FluentActions.Invoking(() => MatchCandidate.Create(FileA, FileB, audioSimilarity: (decimal)score))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Audio_offset_evidence_requires_both_similarity_and_offset()
    {
        FluentActions.Invoking(() => MatchCandidate.Create(FileA, FileB, partialAudioSimilarity: 0.9m))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => MatchCandidate.Create(FileA, FileB, partialAudioOffsetSeconds: 8m))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Audio_offset_evidence_validates_bounds()
    {
        FluentActions.Invoking(() => MatchCandidate.Create(FileA, FileB, partialAudioSimilarity: 0.9m, partialAudioOffsetSeconds: -1m))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() =>
            MatchCandidate.Create(FileA, FileB, partialAudioSimilarity: 0.9m, partialAudioOffsetSeconds: 8m)).Should().NotThrow();
    }
}
