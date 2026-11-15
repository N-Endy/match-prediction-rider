namespace MatchPredictor.Domain.Models;

public sealed record MatchProbabilities(
    double Btts,
    double Over25,
    double Under25,
    double Draw,
    double HomeWin,
    double AwayWin,
    double Over15 = 0.0,
    double DoubleChance1X = 0.0,
    double DoubleChanceX2 = 0.0,
    double DoubleChance12 = 0.0);
