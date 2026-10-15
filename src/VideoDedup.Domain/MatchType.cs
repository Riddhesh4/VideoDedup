namespace VideoDedup.Domain;

public enum MatchType
{
    Different,
    Exact,
    NearDuplicate,
    PartialClip,
    NeedsReview,
}
