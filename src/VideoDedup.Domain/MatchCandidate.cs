namespace VideoDedup.Domain;

/// <summary>Evidence currently available for comparing a pair of files.</summary>
public sealed record MatchCandidate
{
    private MatchCandidate(Guid fileIdA, Guid fileIdB, string? sha256A, string? sha256B, decimal? visualSimilarity, decimal? audioSimilarity)
    {
        FileIdA = fileIdA;
        FileIdB = fileIdB;
        Sha256A = sha256A;
        Sha256B = sha256B;
        VisualSimilarity = visualSimilarity;
        AudioSimilarity = audioSimilarity;
    }

    public Guid FileIdA { get; }
    public Guid FileIdB { get; }
    public string? Sha256A { get; }
    public string? Sha256B { get; }
    public decimal? VisualSimilarity { get; }
    public decimal? AudioSimilarity { get; }

    public static MatchCandidate Create(
        Guid fileIdA,
        Guid fileIdB,
        string? sha256A = null,
        string? sha256B = null,
        decimal? visualSimilarity = null,
        decimal? audioSimilarity = null)
    {
        if (fileIdA == Guid.Empty || fileIdB == Guid.Empty)
        {
            throw new ArgumentException("Both file IDs must be non-empty.");
        }

        if (fileIdA == fileIdB)
        {
            throw new ArgumentException("A file cannot be compared with itself.", nameof(fileIdB));
        }

        return new MatchCandidate(
            fileIdA,
            fileIdB,
            NormalizeHash(sha256A, nameof(sha256A)),
            NormalizeHash(sha256B, nameof(sha256B)),
            NormalizeSimilarity(visualSimilarity, nameof(visualSimilarity)),
            NormalizeSimilarity(audioSimilarity, nameof(audioSimilarity)));
    }

    private static string? NormalizeHash(string? hash, string parameterName)
    {
        if (hash is null)
        {
            return null;
        }

        var normalized = hash.Trim();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", parameterName);
        }

        return normalized.ToUpperInvariant();
    }

    private static decimal? NormalizeSimilarity(decimal? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Similarity must be in [0, 1].");
        }

        return value;
    }
}
