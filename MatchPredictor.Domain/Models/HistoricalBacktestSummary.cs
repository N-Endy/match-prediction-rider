namespace MatchPredictor.Domain.Models;

/// <summary>
/// Persisted rollup from the nightly real-history walk-forward backtest job.
/// </summary>
public class HistoricalBacktestSummary
{
    public int Id { get; set; }
    public DateTime RunAtUtc { get; set; } = DateTime.UtcNow;
    public int SampleCount { get; set; }
    public double BrierScore { get; set; }
    public double ExpectedCalibrationError { get; set; }
    public double LogLoss { get; set; }
    public double FlatStakeRoiPercent { get; set; }
    public double AverageClvPercent { get; set; }
    public int LookbackDays { get; set; }
}
