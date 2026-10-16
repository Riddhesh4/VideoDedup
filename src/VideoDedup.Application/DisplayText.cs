using System.Text;

namespace VideoDedup.Application;

/// <summary>
/// Display hardening for untrusted names. Everything shown in the UI or written to a log passes through here:
/// control characters are dropped, whitespace is collapsed, and length is capped, so a hostile filename can
/// never inject terminal control sequences or blow up a column. Callers render the result as plain text.
/// </summary>
public static class DisplayText
{
    public const int DefaultMaxLength = 160;

    public static string Sanitize(string? value, int maxLength = DefaultMaxLength)
    {
        if (maxLength < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Maximum length must be at least 8.");
        }

        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var pendingSpace = false;
        var length = 0;
        var truncated = false;
        // Rune-by-rune keeps emoji and other astral characters intact while dropping unpaired surrogates.
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsInvisible(rune))
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (length == maxLength)
            {
                truncated = true;
                break;
            }

            builder.Append(rune);
            length++;
        }

        var sanitized = builder.ToString().TrimEnd();
        return truncated ? sanitized + "..." : sanitized;
    }

    private static bool IsInvisible(Rune rune) =>
        Rune.IsControl(rune) || rune.Value is 0x200B or 0x200E or 0x200F or 0x202A or 0x202B or 0x202C or 0x202D or 0x202E or 0xFEFF;

    /// <summary>Sanitized file name without directory components, for lists where the folder is shown separately.</summary>
    public static string FileName(string? path)
    {
        var sanitized = Sanitize(path, 512);
        if (sanitized.Length == 0)
        {
            return string.Empty;
        }

        var index = sanitized.LastIndexOfAny(['/', '\\']);
        return index >= 0 ? Sanitize(sanitized[(index + 1)..]) : Sanitize(sanitized);
    }
}

/// <summary>Joins an indexed root with a stored relative path, refusing anything that could escape the root.</summary>
public static class IndexedPath
{
    /// <summary>
    /// Combines root and relative path. Returns false for empty, rooted, drive-qualified, traversal, or
    /// control-character paths, so callers can refuse to act on a record that does not resolve inside its root.
    /// </summary>
    public static bool TryCombine(string? rootPath, string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (rootPath.Any(char.IsControl) || relativePath.Any(char.IsControl))
        {
            return false;
        }

        var relative = relativePath.Trim().Replace('\\', '/');
        if (relative.StartsWith('/') || relative.StartsWith('\\') ||
            (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':'))
        {
            return false;
        }

        var segments = relative.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        var translated = string.Join(Path.DirectorySeparatorChar, segments);
        fullPath = Path.GetFullPath(Path.Combine(Path.GetFullPath(rootPath), translated));
        return Path.IsPathFullyQualified(fullPath);
    }
}
