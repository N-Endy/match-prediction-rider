namespace MatchPredictor.Domain.Models;

public class MarketCalibrationProfile
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public double BucketStart { get; set; }
    public double BucketEnd { get; set; }
    public int ObservationCount { get; set; }
    public int SuccessCount { get; set; }
    public double CalibratedProbability { get; set; }
    public DateTime LastUpdated { get; set; }
}
