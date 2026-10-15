namespace VideoDedup.Domain;

/// <summary>Versionable score thresholds and weights for duplicate classification.</summary>
public sealed record MatchPolicyOptions
{
    public MatchPolicyOptions(
        decimal nearDuplicateThreshold,
        decimal reviewThreshold,
        decimal conflictHighThreshold,
        decimal conflictLowThreshold,
        decimal visualWeight)
    {
        ValidateUnit(nearDuplicateThreshold, nameof(nearDuplicateThreshold));
        ValidateUnit(reviewThreshold, nameof(reviewThreshold));
        ValidateUnit(conflictHighThreshold, nameof(conflictHighThreshold));
        ValidateUnit(conflictLowThreshold, nameof(conflictLowThreshold));
        ValidateUnit(visualWeight, nameof(visualWeight));

        if (nearDuplicateThreshold <= reviewThreshold)
        {
            throw new ArgumentException("Near-duplicate threshold must be greater than review threshold.", nameof(nearDuplicateThreshold));
        }

        if (conflictHighThreshold <= conflictLowThreshold)
        {
            throw new ArgumentException("Conflict high threshold must be greater than conflict low threshold.", nameof(conflictHighThreshold));
        }

        NearDuplicateThreshold = nearDuplicateThreshold;
        ReviewThreshold = reviewThreshold;
        ConflictHighThreshold = conflictHighThreshold;
        ConflictLowThreshold = conflictLowThreshold;
        VisualWeight = visualWeight;
    }

    public decimal NearDuplicateThreshold { get; }
    public decimal ReviewThreshold { get; }
    public decimal ConflictHighThreshold { get; }
    public decimal ConflictLowThreshold { get; }
    public decimal VisualWeight { get; }
    public decimal AudioWeight => 1m - VisualWeight;

    public static MatchPolicyOptions Default { get; } = new(
        nearDuplicateThreshold: 0.90m,
        reviewThreshold: 0.70m,
        conflictHighThreshold: 0.90m,
        conflictLowThreshold: 0.20m,
        visualWeight: 0.60m);

    private static void ValidateUnit(decimal value, string parameterName)
    {
        if (value is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be in [0, 1].");
        }
    }
}
