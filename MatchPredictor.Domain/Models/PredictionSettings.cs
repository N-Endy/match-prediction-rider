namespace MatchPredictor.Domain.Models;

public class PredictionSettings
{
    public double HomeWinStrong { get; set; } = 0.68;
    public double AwayWinStrong { get; set; } = 0.70;
    public double BttsScoreThreshold { get; set; } = 0.55;
    public double OverTwoGoalsStrongThreshold { get; set; } = 0.58;
    public double DrawStrongThreshold { get; set; } = 0.54;
    public double MinTotalXgRequiredForWin { get; set; } = 2.0;
}
