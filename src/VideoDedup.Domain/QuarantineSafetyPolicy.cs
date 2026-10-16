namespace VideoDedup.Domain;

/// <summary>Why a file must not be moved to quarantine. Reasons are stable identifiers the UI can localize or explain.</summary>
public enum QuarantineBlockReason
{
    /// <summary>The path does not resolve right now: removed, unplugged drive, or an unreachable root.</summary>
    FileMissing,

    /// <summary>The record points outside its configured root, so acting on it could touch the wrong file.</summary>
    PathOutsideRoot,

    /// <summary>Reparse points are never quarantined; the target is not the indexed file.</summary>
    SymbolicLink,

    /// <summary>The last scan could not read this root.</summary>
    RootUnavailable,

    /// <summary>The root is disabled, so its records are not current.</summary>
    RootDisabled,

    /// <summary>No other indexed copy of the same content is currently present.</summary>
    LastRemainingCopy,
}

/// <summary>
/// Facts about one quarantining decision. Everything is gathered outside the Domain so this policy stays pure and
/// deterministic; callers never pass paths (path containment is decided before the fact reaches here).
/// </summary>
public sealed record QuarantineSafetyFacts(
    bool FileExists,
    bool IsSymbolicLink,
    bool IsInsideRoot,
    bool RootAvailable,
    bool RootEnabled,
    bool HasContentHash,
    int OtherAvailableCopies);

/// <summary>Outcome of evaluating <see cref="QuarantineSafetyFacts"/>; a blocked verdict always explains itself.</summary>
public sealed record QuarantineSafetyVerdict
{
    private QuarantineSafetyVerdict(QuarantineBlockReason? blockedBy, IReadOnlyList<string> reasons)
    {
        BlockedBy = blockedBy;
        Reasons = Array.AsReadOnly(reasons.ToArray());
    }

    public QuarantineBlockReason? BlockedBy { get; }

    public bool IsAllowed => BlockedBy is null;

    public IReadOnlyList<string> Reasons { get; }

    public static QuarantineSafetyVerdict Allowed(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        return new QuarantineSafetyVerdict(null, Normalize(reasons, nameof(reasons)));
    }

    public static QuarantineSafetyVerdict Blocked(QuarantineBlockReason reason, IEnumerable<string> reasons)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        ArgumentNullException.ThrowIfNull(reasons);
        var normalized = Normalize(reasons, nameof(reasons));
        if (normalized.Count == 0)
        {
            throw new ArgumentException("A blocked quarantine verdict must explain why.", nameof(reasons));
        }

        return new QuarantineSafetyVerdict(reason, normalized);
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> reasons, string parameterName)
    {
        var normalized = reasons.Select(reason => reason?.Trim())
            .Where(reason => !string.IsNullOrEmpty(reason))
            .Select(reason => reason!)
            .ToArray();
        if (normalized.Any(reason => reason.Any(char.IsControl)))
        {
            throw new ArgumentException("Quarantine reasons cannot contain control characters.", parameterName);
        }

        return normalized;
    }
}

/// <summary>
/// Quarantine-first safety rule: a file may only be moved when it is reachable, lives inside its configured root,
/// is a real file (not a reparse point), belongs to an enabled and readable root, and at least one other indexed
/// copy of the same content is present. Unknown content state is treated as "no other copy" on purpose, because
/// under-detecting copies risks the only copy of a clip.
/// </summary>
public static class QuarantineSafetyPolicy
{
    public static QuarantineSafetyVerdict Evaluate(QuarantineSafetyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.OtherAvailableCopies < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(facts), "Other copy count cannot be negative.");
        }

        if (!facts.IsInsideRoot)
        {
            return QuarantineSafetyVerdict.Blocked(QuarantineBlockReason.PathOutsideRoot,
                ["The record is not inside its configured scan root."]);
        }

        if (!facts.RootEnabled)
        {
            return QuarantineSafetyVerdict.Blocked(QuarantineBlockReason.RootDisabled,
                ["The scan root is disabled, so its file records are not current."]);
        }

        if (!facts.RootAvailable)
        {
            return QuarantineSafetyVerdict.Blocked(QuarantineBlockReason.RootUnavailable,
                ["The scan root is currently unavailable; offline or unplugged files cannot be quarantined."]);
        }

        if (!facts.FileExists)
        {
            return QuarantineSafetyVerdict.Blocked(QuarantineBlockReason.FileMissing,
                ["The file is not reachable right now, so there is nothing safe to move."]);
        }

        if (facts.IsSymbolicLink)
        {
            return QuarantineSafetyVerdict.Blocked(QuarantineBlockReason.SymbolicLink,
                ["The entry is a link or reparse point rather than the indexed file."]);
        }

        if (facts.OtherAvailableCopies < 1)
        {
            return QuarantineSafetyVerdict.Blocked(QuarantineBlockReason.LastRemainingCopy,
                facts.HasContentHash
                    ? ["No other indexed file currently holds the same content, so this is the last copy."]
                    : ["This file has no content hash yet, so the scanner cannot prove another copy exists."]);
        }

        return QuarantineSafetyVerdict.Allowed(facts.HasContentHash
            ? [$"{facts.OtherAvailableCopies} other indexed copy/copies of the same content are available."]
            : [$"{facts.OtherAvailableCopies} other indexed copy/copies are available."]);
    }
}
