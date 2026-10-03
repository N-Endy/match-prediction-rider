namespace MatchPredictor.Domain.Models;

public class ModelShadowEvaluation
{
    public long Id { get; set; }
    public int PredictionId { get; set; }
    public string FixtureKey { get; set; } = string.Empty;
    public PredictionMarket Market { get; set; }
    public string PredictedOutcome { get; set; } = string.Empty;
    public double ChampionCalibratedProbability { get; set; }
    public double ChallengerCalibratedProbability { get; set; }
    public bool? OutcomeOccurred { get; set; }
    public double? ChampionBrierLoss { get; set; }
    public double? ChallengerBrierLoss { get; set; }
    public bool IsSettled { get; set; }
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAtUtc { get; set; }
}
