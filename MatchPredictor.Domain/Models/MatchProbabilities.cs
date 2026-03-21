namespace MatchPredictor.Domain.Models;

public sealed record MatchProbabilities(
    double Over25Sets,
    double Under25Sets,
    double HomeWin,
    double AwayWin,
    double HomeSetHandicap,
    double AwaySetHandicap);
