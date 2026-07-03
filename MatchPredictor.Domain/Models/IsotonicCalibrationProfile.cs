namespace MatchPredictor.Domain.Models;

/// <summary>
/// Piecewise-constant isotonic calibration map for a market. KnotsJson stores
/// ascending (predicted, calibrated) pairs fitted via pool-adjacent-violators.
/// </summary>
public class IsotonicCalibrationProfile
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public string KnotsJson { get; set; } = "[]";
    public int TrainingSampleCount { get; set; }
    public int ValidationSampleCount { get; set; }
    public double BaselineBrierScore { get; set; }
    public double ValidationBrierScore { get; set; }
    public double Improvement { get; set; }
    public bool IsRecommended { get; set; }
    public DateTime LastUpdated { get; set; }
}
