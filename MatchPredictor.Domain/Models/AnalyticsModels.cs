namespace MatchPredictor.Domain.Models;

public class AnalyticsStats
{
    public int TotalPredictions { get; set; }
    public int CompletedPredictions { get; set; }
    public int CorrectPredictions { get; set; }
    public double OverallAccuracy { get; set; }
    public int SettledForecasts { get; set; }
    public double RawBrierScore { get; set; }
    public double BrierScore { get; set; }
    public Dictionary<string, CategoryStat> CategoryStats { get; set; } = new();
    public List<ForecastMarketStat> ForecastMarketStats { get; set; } = [];
    public List<PromotionTimelineItem> PromotionTimeline { get; set; } = [];
}

public class CategoryStat
{
    public string Category { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Correct { get; set; }
    public double Accuracy { get; set; }
    public double BrierScore { get; set; }
}

public class ForecastMarketStat
{
    public PredictionMarket Market { get; set; }
    public string MarketName { get; set; } = string.Empty;
    public int SettledCount { get; set; }
    public string ActiveCalibrator { get; set; } = "Bucket";
    public double FallbackThreshold { get; set; }
    public double ActiveThreshold { get; set; }
    public string ThresholdSource { get; set; } = "Configured";
    public int ThresholdSampleCount { get; set; }
    public double ThresholdHitRate { get; set; }
    public double ThresholdPublishedPerWeek { get; set; }
    public double ThresholdBrierScore { get; set; }
    public DateTime? ThresholdLastUpdated { get; set; }
    public double RawBrierScore { get; set; }
    public double CalibratedBrierScore { get; set; }
    public double? BetaBaselineBrierScore { get; set; }
    public double? BetaValidationBrierScore { get; set; }
    public double? BetaImprovement { get; set; }
    public bool BetaRecommended { get; set; }
    public int? BetaTrainingSampleCount { get; set; }
    public int? BetaValidationSampleCount { get; set; }
    public DateTime? BetaLastUpdated { get; set; }
    public BrierDecomposition RawDecomposition { get; set; } = new();
    public BrierDecomposition CalibratedDecomposition { get; set; } = new();
    public List<ReliabilityCurvePoint> RawReliabilityCurve { get; set; } = [];
    public List<ReliabilityCurvePoint> CalibratedReliabilityCurve { get; set; } = [];
    public List<EraPerformanceStat> CalibratorEraStats { get; set; } = [];
    public List<EraPerformanceStat> ThresholdEraStats { get; set; } = [];
}

public class BrierDecomposition
{
    public double Score { get; set; }
    public double Reliability { get; set; }
    public double Resolution { get; set; }
    public double Uncertainty { get; set; }
}

public class ReliabilityCurvePoint
{
    public double BucketStart { get; set; }
    public double BucketEnd { get; set; }
    public double AveragePredictedProbability { get; set; }
    public double ObservedFrequency { get; set; }
    public int Count { get; set; }
}

public class EraPerformanceStat
{
    public string Era { get; set; } = string.Empty;
    public int Count { get; set; }
    public double HitRate { get; set; }
    public double BrierScore { get; set; }
}

public class PromotionTimelineItem
{
    public DateTime EffectiveAt { get; set; }
    public string MarketName { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public double? Improvement { get; set; }
}
