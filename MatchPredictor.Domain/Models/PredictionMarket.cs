namespace MatchPredictor.Domain.Models;

public enum PredictionMarket
{
    BothTeamsScore = 0,
    Over25Goals = 1,
    StraightWin = 2,
    Draw = 3
}

public static class PredictionMarketExtensions
{
    public static string ToCategory(this PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => "BothTeamsScore",
            PredictionMarket.Over25Goals => "Over2.5Goals",
            PredictionMarket.StraightWin => "StraightWin",
            PredictionMarket.Draw => "Draw",
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null)
        };
    }

    public static bool TryFromCategory(string? category, out PredictionMarket market)
    {
        market = category switch
        {
            "BothTeamsScore" => PredictionMarket.BothTeamsScore,
            "Over2.5Goals" => PredictionMarket.Over25Goals,
            "StraightWin" => PredictionMarket.StraightWin,
            "Draw" => PredictionMarket.Draw,
            _ => default
        };

        return category is "BothTeamsScore" or "Over2.5Goals" or "StraightWin" or "Draw";
    }
}
