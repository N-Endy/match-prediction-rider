namespace MatchPredictor.Domain.Models;

public class MetaModelProfile
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public double Intercept { get; set; }
    public double Slope { get; set; } = 1.0;
    public int TrainingSampleCount { get; set; }
    public int ValidationSampleCount { get; set; }
    public double BaselineBrierScore { get; set; }
    public double CandidateBrierScore { get; set; }
    public double Improvement { get; set; }
    public bool IsPromoted { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
