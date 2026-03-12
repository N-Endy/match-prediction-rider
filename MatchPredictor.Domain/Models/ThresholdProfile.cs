namespace MatchPredictor.Domain.Models;

public class ThresholdProfile
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public double BaselineThreshold { get; set; }
    public double Threshold { get; set; }
    public int SampleCount { get; set; }
    public double HitRate { get; set; }
    public double PublishedPerWeek { get; set; }
    public double AverageCalibratedProbability { get; set; }
    public double ObservedFrequency { get; set; }
    public double BrierScore { get; set; }
    public int TrainingSampleCount { get; set; }
    public int ValidationSampleCount { get; set; }
    public double BaselineHitRate { get; set; }
    public double BaselineBrierScore { get; set; }
    public double Improvement { get; set; }
    public bool IsPromoted { get; set; }
    public DateTime LastUpdated { get; set; }
}
