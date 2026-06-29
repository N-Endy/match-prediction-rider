namespace MatchPredictor.Domain.Sourcing;

/// <summary>
/// A single source's observation of a fixture's score, used as a vote in consensus resolution.
/// </summary>
public sealed record ScoreCandidate(
    string SourceName,
    string NormalizedScore,
    bool IsFinished,
    double Reliability,
    DateTime ObservedAtUtc);

/// <summary>
/// The outcome of reconciling multiple sources' score observations for one fixture.
/// </summary>
public sealed record ScoreConsensusResult(
    string? Score,
    bool IsFinished,
    string? WinningSource,
    int AgreeingSources,
    int TotalSources,
    double AgreementRatio,
    double WinningWeight,
    bool HasConsensus)
{
    public static ScoreConsensusResult Empty { get; } = new(null, false, null, 0, 0, 0.0, 0.0, false);
}

/// <summary>
/// Pure, deterministic voting layer that reconciles conflicting score observations from
/// multiple data sources. A single scraper failure cannot blank a fixture as long as another
/// source reported it, and disagreements are resolved by reliability- and recency-weighted voting
/// that favours finished results over in-play snapshots.
/// </summary>
public static class ScoreConsensusResolver
{
    private const double MinReliability = 0.1;
    private const double MaxReliability = 1.0;
    private const double FinishedWeightMultiplier = 1.5;

    public static ScoreConsensusResult Resolve(
        IEnumerable<ScoreCandidate> candidates,
        double consensusThreshold = 0.5)
    {
        var valid = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.NormalizedScore))
            .Select(candidate => candidate with { NormalizedScore = candidate.NormalizedScore.Trim() })
            .ToList();

        if (valid.Count == 0)
        {
            return ScoreConsensusResult.Empty;
        }

        var groups = valid
            .GroupBy(candidate => candidate.NormalizedScore, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Score = group.Key,
                Weight = group.Sum(Weight),
                Count = group.Count(),
                IsFinished = group.Any(candidate => candidate.IsFinished),
                Best = group
                    .OrderByDescending(candidate => candidate.IsFinished)
                    .ThenByDescending(candidate => Clamp(candidate.Reliability))
                    .ThenByDescending(candidate => candidate.ObservedAtUtc)
                    .First()
            })
            .OrderByDescending(group => group.IsFinished)
            .ThenByDescending(group => group.Weight)
            .ThenByDescending(group => group.Count)
            .ToList();

        var winner = groups[0];
        var agreementRatio = winner.Count / (double)valid.Count;

        return new ScoreConsensusResult(
            Score: winner.Score,
            IsFinished: winner.IsFinished,
            WinningSource: winner.Best.SourceName,
            AgreeingSources: winner.Count,
            TotalSources: valid.Count,
            AgreementRatio: agreementRatio,
            WinningWeight: winner.Weight,
            HasConsensus: valid.Count == 1 || agreementRatio >= consensusThreshold);
    }

    private static double Weight(ScoreCandidate candidate)
    {
        var reliabilityWeight = Clamp(candidate.Reliability);
        return reliabilityWeight * (candidate.IsFinished ? FinishedWeightMultiplier : 1.0);
    }

    private static double Clamp(double reliability) => Math.Clamp(reliability, MinReliability, MaxReliability);
}
