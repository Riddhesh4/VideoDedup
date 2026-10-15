namespace VideoDedup.Domain;

public interface IDuplicateMatchPolicy
{
    MatchResult Evaluate(MatchCandidate candidate);
}
