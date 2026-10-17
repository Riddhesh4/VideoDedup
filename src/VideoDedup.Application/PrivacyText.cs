using System.Text;
using System.Text.RegularExpressions;

namespace VideoDedup.Application;

/// <summary>
/// Text hygiene for anything that leaves the process: logs, the dashboard, and exported diagnostics. Local media
/// names and folders are the user's private data, so a failure message must be able to carry the *kind* of failure
/// without carrying the path that caused it.
/// </summary>
public static partial class PrivacyText
{
    public const int MaxLength = 200;

    public const int MaxFailureLength = 128;

    public const string PathPlaceholder = "<path>";

    private const string TrailingPunctuation = ".,;:)]}'\"!?";

    /// <summary>Collapses whitespace, removes control and formatting characters, and caps the length.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLength));
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    public static string? SanitizeOptional(string? value)
    {
        var sanitized = Sanitize(value);
        return sanitized.Length == 0 ? null : sanitized;
    }

    /// <summary>Sanitizes text and replaces every drive-qualified or UNC path with <see cref="PathPlaceholder"/>.</summary>
    public static string RedactPaths(string? value) =>
        ReplacePaths(UncPath(), ReplacePaths(WindowsPath(), Sanitize(value)));

    /// <summary>Redaction for a failure string that is about to be persisted, so it is also length-bounded.</summary>
    public static string? RedactPathsOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var redacted = RedactPaths(value);
        if (redacted.Length > MaxFailureLength)
        {
            redacted = redacted[..MaxFailureLength];
        }

        return redacted.Length == 0 ? null : redacted;
    }

    private static string ReplacePaths(Regex pattern, string input)
    {
        if (input.Length == 0)
        {
            return input;
        }

        return pattern.Replace(input, match =>
        {
            var value = match.Value;
            var end = value.Length;
            while (end > 0 && TrailingPunctuation.Contains(value[end - 1]))
            {
                end--;
            }

            // A bare root ("C:\") is not a leaked location worth rewriting.
            return end < 4 ? value : string.Concat(PathPlaceholder, value.AsSpan(end));
        });
    }

    // Known limit: a path containing spaces is redacted up to its first space. Failure kinds in this application are
    // always "Type: Reason" pairs, so a path is never the primary payload; this redaction is defence in depth.
    // See docs/PRIVACY.md.
    [GeneratedRegex(@"[A-Za-z]:\\[^\s""'<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\\\\[^\s""'<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex UncPath();
}
