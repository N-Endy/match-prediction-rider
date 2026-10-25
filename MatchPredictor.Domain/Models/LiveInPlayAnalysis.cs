namespace MatchPredictor.Domain.Models;

public class LiveInPlayMarketRecommendation
{
    public string Tier { get; set; } = "Primary"; // "Primary Best Read", "Value / Momentum Angle", "Safety / Hedge"
    public string Market { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public double? EstimatedOdds { get; set; }
    public string TacticalRationale { get; set; } = string.Empty;
    public string KeyRisk { get; set; } = string.Empty;
    public string ActionKey { get; set; } = string.Empty;
}

public class LiveInPlayAnalysis
{
    public int PredictionId { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public DateTime? MatchDateTimeUtc { get; set; }
    public string LiveScore { get; set; } = string.Empty;
    public int? ElapsedMinutes { get; set; }
    public string MatchPhase { get; set; } = string.Empty;

    public string PreMatchMarket { get; set; } = string.Empty;
    public string PreMatchOutcome { get; set; } = string.Empty;
    public double? PreMatchConfidence { get; set; }
    public double? PreMatchOdds { get; set; }

    public string InPlayMarket { get; set; } = string.Empty;
    public string InPlayOutcome { get; set; } = string.Empty;
    public double InPlayConfidence { get; set; }
    public double? InPlayOdds { get; set; }

    public string ContrastSummary { get; set; } = string.Empty;
    public string TacticalReasoning { get; set; } = string.Empty;
    public string KeyRisk { get; set; } = string.Empty;
    public string ActionKey { get; set; } = string.Empty;
    public List<string> LiveInsightBullets { get; set; } = [];

    public List<LiveInPlayMarketRecommendation> Recommendations { get; set; } = [];
}
