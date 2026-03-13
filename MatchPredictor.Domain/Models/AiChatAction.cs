namespace MatchPredictor.Domain.Models;

public class AiChatAction
{
    public string Type { get; set; } = "add_bet";
    public string ActionKey { get; set; } = string.Empty;
    public int PredictionId { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty;
    public string Prediction { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
}
