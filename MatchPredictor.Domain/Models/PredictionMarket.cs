namespace MatchPredictor.Domain.Models;

public enum PredictionMarket
{
    HomeWin = 0,
    AwayWin = 1,
    Over25Sets = 2,
    Under25Sets = 3,
    HomeSetHandicap = 4,
    AwaySetHandicap = 5
}

public static class PredictionMarketExtensions
{
    public static string ToCategory(this PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => "MatchWinner",
            PredictionMarket.AwayWin => "MatchWinner",
            PredictionMarket.Over25Sets => "OverUnderSets",
            PredictionMarket.Under25Sets => "OverUnderSets",
            PredictionMarket.HomeSetHandicap => "SetHandicap",
            PredictionMarket.AwaySetHandicap => "SetHandicap",
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null)
        };
    }

    public static string ToDisplayName(this PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => "Home Win",
            PredictionMarket.AwayWin => "Away Win",
            PredictionMarket.Over25Sets => "Over 2.5 Sets",
            PredictionMarket.Under25Sets => "Under 2.5 Sets",
            PredictionMarket.HomeSetHandicap => "Home Set Handicap",
            PredictionMarket.AwaySetHandicap => "Away Set Handicap",
            _ => market.ToString()
        };
    }

    public static bool TryFromCategory(string? category, out PredictionMarket market)
    {
        market = category switch
        {
            "MatchWinner" => PredictionMarket.HomeWin,
            "OverUnderSets" => PredictionMarket.Over25Sets,
            "SetHandicap" => PredictionMarket.HomeSetHandicap,
            _ => default
        };

        return category is "MatchWinner" or "OverUnderSets" or "SetHandicap";
    }
}
