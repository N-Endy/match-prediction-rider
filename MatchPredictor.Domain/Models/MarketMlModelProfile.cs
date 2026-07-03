namespace MatchPredictor.Domain.Models;

public class MarketMlModelProfile
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public byte[] ModelBytes { get; set; } = [];
    public int TrainingSampleCount { get; set; }
    public int HoldoutSampleCount { get; set; }
    public double BaselineBrierScore { get; set; }
    public double CandidateBrierScore { get; set; }
    public double Improvement { get; set; }
    public bool IsPromoted { get; set; }
    public string FeatureSchemaJson { get; set; } = "[]";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
