namespace MatchPredictor.Domain.Models;

public class AiChatAction
{
    public string Type { get; set; } = "add_bet";
    public string ActionKey { get; set; } = string.Empty;
    public int PredictionId { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string MatchDateLabel { get; set; } = string.Empty;
    public string KickoffTime { get; set; } = string.Empty;
    public DateTime? MatchDateTimeUtc { get; set; }
    public string Status { get; set; } = "Upcoming";
    public string? ActualScore { get; set; }
    public string Market { get; set; } = string.Empty;
    public string Prediction { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public double? ModelProbability { get; set; }
    public double? MarketProbability { get; set; }
    public double? EdgePoints { get; set; }
    public double? EstimatedOdds { get; set; }
    public string? AnalysisSummary { get; set; }
    public string? AnalysisConfidence { get; set; }
    public List<string> InsightBullets { get; set; } = [];
    public string? InsightSource { get; set; }
    public bool CanBook { get; set; } = true;
}
