namespace MatchPredictor.Domain.Models;

public class BetaCalibrationProfile
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public double Alpha { get; set; }
    public double Beta { get; set; }
    public double Gamma { get; set; }
    public int TrainingSampleCount { get; set; }
    public int ValidationSampleCount { get; set; }
    public double BaselineBrierScore { get; set; }
    public double ValidationBrierScore { get; set; }
    public double Improvement { get; set; }
    public bool IsRecommended { get; set; }
    public DateTime LastUpdated { get; set; }
}
