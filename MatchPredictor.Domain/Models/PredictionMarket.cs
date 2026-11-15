namespace MatchPredictor.Domain.Models;

public enum PredictionMarket
{
    BothTeamsScore = 0,
    Over25Goals = 1,
    StraightWin = 2,
    Draw = 3,
    HomeWin = 4,
    AwayWin = 5,
    Under25Goals = 6,
    Over15Goals = 7,
    DoubleChance = 8
}

public static class PredictionMarketExtensions
{
    public static string ToCategory(this PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => "BothTeamsScore",
            PredictionMarket.Over25Goals => "Over2.5Goals",
            PredictionMarket.Under25Goals => "Under2.5Goals",
            PredictionMarket.HomeWin => "StraightWin",
            PredictionMarket.AwayWin => "StraightWin",
            PredictionMarket.StraightWin => "StraightWin",
            PredictionMarket.Draw => "Draw",
            PredictionMarket.Over15Goals => "Over1.5Goals",
            PredictionMarket.DoubleChance => "DoubleChance",
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null)
        };
    }

    public static string ToDisplayName(this PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => "BTTS",
            PredictionMarket.Over25Goals => "Over 2.5",
            PredictionMarket.Under25Goals => "Under 2.5",
            PredictionMarket.Draw => "Draw",
            PredictionMarket.HomeWin => "Home Win",
            PredictionMarket.AwayWin => "Away Win",
            PredictionMarket.StraightWin => "Straight Win",
            PredictionMarket.Over15Goals => "Over 1.5",
            PredictionMarket.DoubleChance => "Double Chance",
            _ => market.ToString()
        };
    }

    public static bool TryFromCategory(string? category, out PredictionMarket market)
    {
        market = category switch
        {
            "BothTeamsScore" => PredictionMarket.BothTeamsScore,
            "Over2.5Goals" => PredictionMarket.Over25Goals,
            "Under2.5Goals" => PredictionMarket.Under25Goals,
            "StraightWin" => PredictionMarket.StraightWin,
            "Draw" => PredictionMarket.Draw,
            "Over1.5Goals" => PredictionMarket.Over15Goals,
            "DoubleChance" => PredictionMarket.DoubleChance,
            _ => default
        };

        return category is "BothTeamsScore" or "Over2.5Goals" or "Under2.5Goals" or "StraightWin" or "Draw" or "Over1.5Goals" or "DoubleChance";
    }
}
