namespace MatchPredictor.Domain.Models;

public static class PredictionThresholds
{
    // Adjusted downward to account for Vig-removal
    public const double HomeWinStrong = 0.68; 
    public const double AwayWinStrong = 0.7;

    // Set to 0.55 because Poisson math exposes that true BTTS rarely exceeds 60%
    public const double BttsScoreThreshold = 0.55;

    // Adjusted downward to account for Vig-removal
    public const double OverTwoGoalsStrongThreshold = 0.58;

    // True mathematical draws rarely exceed 35%
    public const double DrawStrongThreshold = 0.54; 
}