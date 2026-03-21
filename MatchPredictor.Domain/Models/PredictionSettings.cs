namespace MatchPredictor.Domain.Models;

public class PredictionSettings
{
    public double HomeWinStrong { get; set; } = 0.68;
    public double AwayWinStrong { get; set; } = 0.70;
    public double OverTwoPointFiveSetsStrongThreshold { get; set; } = 0.58;
    public double UnderTwoPointFiveSetsStrongThreshold { get; set; } = 0.58;
    public double HomeSetHandicapStrongThreshold { get; set; } = 0.60;
    public double AwaySetHandicapStrongThreshold { get; set; } = 0.60;
    public double ValueBetMinimumEdge { get; set; } = 0.03;
}
