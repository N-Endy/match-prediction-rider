namespace MatchPredictor.Domain.Models;

/// <summary>
/// Persisted segment calibration adjustment per league and market.
/// </summary>
public class LeagueCalibrationProfile
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public string League { get; set; } = string.Empty;
    public double LogitAdjustment { get; set; }
    public int SampleCount { get; set; }
    public DateTime LastUpdated { get; set; }
}
