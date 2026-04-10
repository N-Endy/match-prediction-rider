namespace MatchPredictor.Domain.Models;

public class AiChatFootballInsightRequest
{
    public string ActionKey { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string PredictionCategory { get; set; } = string.Empty;
    public string PredictedOutcome { get; set; } = string.Empty;
    public DateOnly MatchLocalDate { get; set; }
    public string KickoffTime { get; set; } = string.Empty;
    public DateTime? MatchDateTimeUtc { get; set; }
}
