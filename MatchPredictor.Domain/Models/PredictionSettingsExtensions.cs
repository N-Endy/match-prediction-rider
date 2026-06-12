namespace MatchPredictor.Domain.Models;

public static class PredictionSettingsExtensions
{
    public static double ResolveFallbackThreshold(this PredictionSettings settings, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => settings.BttsScoreThreshold,
            PredictionMarket.Over25Goals => settings.OverTwoGoalsStrongThreshold,
            PredictionMarket.Under25Goals => settings.UnderTwoGoalsStrongThreshold,
            PredictionMarket.Draw => settings.DrawStrongThreshold,
            PredictionMarket.HomeWin => settings.HomeWinStrong,
            PredictionMarket.AwayWin => settings.AwayWinStrong,
            _ => 0.0
        };
    }
}
