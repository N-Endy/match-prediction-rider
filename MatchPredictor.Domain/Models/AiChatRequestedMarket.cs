namespace MatchPredictor.Domain.Models;

public class AiChatRequestedMarket
{
    public string PredictionCategory { get; set; } = string.Empty;
    public int? Count { get; set; }
    public bool ExplicitCount { get; set; }

    public string DisplayName => PredictionCategory switch
    {
        "BothTeamsScore" => "BTTS",
        "Over1.5Goals" => "Over 1.5",
        "Over2.5Goals" => "Over 2.5",
        "Under2.5Goals" => "Under 2.5",
        "DoubleChance" => "Double Chance",
        "Draw" => "Draw",
        "StraightWin" => "Straight Win",
        _ => PredictionCategory
    };
}
