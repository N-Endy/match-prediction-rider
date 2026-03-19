namespace MatchPredictor.Domain.Models;

public sealed record MatchProbabilities(
    double Btts,
    double Over25,
    double Under25,
    double Draw,
    double HomeWin,
    double AwayWin);
