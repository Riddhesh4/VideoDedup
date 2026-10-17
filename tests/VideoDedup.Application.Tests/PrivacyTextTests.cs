using VideoDedup.Application;

namespace VideoDedup.Application.Tests;

public sealed class PrivacyTextTests
{
    [Fact]
    public void Sanitize_removes_control_characters_and_collapses_whitespace()
    {
        PrivacyText.Sanitize("  broken\u0000\r\n  name\t ").Should().Be("broken name");
        PrivacyText.Sanitize(null).Should().BeEmpty();
        PrivacyText.Sanitize("   ").Should().BeEmpty();
        PrivacyText.SanitizeOptional("  ").Should().BeNull();
    }

    [Fact]
    public void Sanitize_caps_the_length_of_hostile_input()
    {
        PrivacyText.Sanitize(new string('a', 5000)).Should().HaveLength(PrivacyText.MaxLength);
    }

    [Theory]
    [InlineData(@"C:\Users\bob\private video.mkv", "bob", @"C:\Users")]
    [InlineData(@"\\nas\share\holiday.mkv", "holiday", "nas")]
    [InlineData("failed at D:\\Movies\\x.mkv while reading", "Movies", "D:\\")]
    public void RedactPaths_removes_local_locations_from_failure_text(string message, string firstSecret, string secondSecret)
    {
        var redacted = PrivacyText.RedactPaths(message);

        redacted.Should().Contain(PrivacyText.PathPlaceholder);
        redacted.Should().NotContain(firstSecret);
        redacted.Should().NotContain(secondSecret);
    }

    [Fact]
    public void RedactPaths_keeps_sentence_punctuation_and_bounded_length()
    {
        PrivacyText.RedactPaths(@"Could not read C:\gone\clip.mkv.").Should().Be($"Could not read {PrivacyText.PathPlaceholder}.");

        var bounded = PrivacyText.RedactPathsOptional(new string('x', 400) + @" C:\users\bob\a.mkv");
        bounded.Should().NotBeNull();
        bounded!.Length.Should().BeLessThanOrEqualTo(PrivacyText.MaxFailureLength);
        bounded.Should().NotContain("bob");
    }

    [Fact]
    public void RedactPaths_leaves_ordinary_failure_text_alone()
    {
        PrivacyText.RedactPaths("MediaProcessingException: Timeout")
            .Should().Be("MediaProcessingException: Timeout");
        PrivacyText.RedactPathsOptional(null).Should().BeNull();
    }
}
