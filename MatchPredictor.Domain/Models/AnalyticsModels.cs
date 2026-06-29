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
    public double LogLoss { get; set; }
    public double RawExpectedCalibrationError { get; set; }
    public double ExpectedCalibrationError { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1Score { get; set; }
    public Dictionary<string, CategoryStat> CategoryStats { get; set; } = new();
    public List<ForecastMarketStat> ForecastMarketStats { get; set; } = [];
    public List<ConfidenceBandStat> ConfidenceBandStats { get; set; } = [];
    public List<LeagueSegmentStat> LeagueSegmentStats { get; set; } = [];
    public List<SourceSegmentStat> SourceSegmentStats { get; set; } = [];
    public List<PromotionTimelineItem> PromotionTimeline { get; set; } = [];
    public BettingPerformanceStats BettingPerformance { get; set; } = new();
    public List<ForecastFeatureDiagnostic> FeatureDiagnostics { get; set; } = [];
}

public class ForecastFeatureDiagnostic
{
    public DateTime? MatchDateTime { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string MarketName { get; set; } = string.Empty;
    public string PredictedOutcome { get; set; } = string.Empty;
    public double RawProbability { get; set; }
    public double CalibratedProbability { get; set; }
    public bool? OutcomeOccurred { get; set; }
    public bool StatisticalSignalApplied { get; set; }
    public List<FeatureContributionItem> Contributions { get; set; } = [];
}

public class FeatureContributionItem
{
    public string Group { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double? Value { get; set; }
}

public class BettingPerformanceStats
{
    public int SettledBetCount { get; set; }
    public int WinningBetCount { get; set; }
    public double WinRate { get; set; }
    public double TotalStakedUnits { get; set; }
    public double NetProfitUnits { get; set; }
    public double RoiPercent { get; set; }
    public double YieldPercent { get; set; }
    public double MaxDrawdownUnits { get; set; }
    public double AverageOdds { get; set; }
    public double KellyFraction { get; set; }
    public double KellyStakedUnits { get; set; }
    public double KellyNetProfitUnits { get; set; }
    public double KellyRoiPercent { get; set; }
    public int KellyBetCount { get; set; }
    public int ClosingLineSamples { get; set; }
    public double AverageClosingLineValuePercent { get; set; }
    public double BeatCloseRate { get; set; }
    public List<MarketBettingStat> Markets { get; set; } = [];
}

public class MarketBettingStat
{
    public string MarketKey { get; set; } = string.Empty;
    public string MarketName { get; set; } = string.Empty;
    public int SettledBetCount { get; set; }
    public int WinningBetCount { get; set; }
    public double WinRate { get; set; }
    public double NetProfitUnits { get; set; }
    public double RoiPercent { get; set; }
    public double AverageOdds { get; set; }
    public int ClosingLineSamples { get; set; }
    public double AverageClosingLineValuePercent { get; set; }
}

public class AnalyticsLiveConfigSnapshot
{
    public DateTime GeneratedAtLocal { get; set; }
    public List<LiveMarketConfigStat> Markets { get; set; } = [];
    public List<PromotionTimelineItem> PromotionTimeline { get; set; } = [];
}

public class CategoryStat
{
    public string Category { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Correct { get; set; }
    public double Accuracy { get; set; }
    public double BrierScore { get; set; }
    public double LogLoss { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1Score { get; set; }
}

public class ForecastMarketStat
{
    public PredictionMarket Market { get; set; }
    public string MarketName { get; set; } = string.Empty;
    public int SettledCount { get; set; }
    public double HitRate { get; set; }
    public double LogLoss { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1Score { get; set; }
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
    public double RawExpectedCalibrationError { get; set; }
    public double CalibratedExpectedCalibrationError { get; set; }
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

public class ConfidenceBandStat
{
    public double MinProbability { get; set; }
    public double MaxProbability { get; set; }
    public int SampleCount { get; set; }
    public double HitRate { get; set; }
    public double AverageProbability { get; set; }
    public double BrierScore { get; set; }
    public double LogLoss { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1Score { get; set; }
}

public class LeagueSegmentStat
{
    public string League { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public double HitRate { get; set; }
    public double BrierScore { get; set; }
    public double LogLoss { get; set; }
}

public class SourceSegmentStat
{
    public string SourceName { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public double HitRate { get; set; }
    public double BrierScore { get; set; }
    public double LogLoss { get; set; }
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

public class LiveMarketConfigStat
{
    public PredictionMarket Market { get; set; }
    public string MarketName { get; set; } = string.Empty;
    public string ActiveCalibrator { get; set; } = "Bucket";
    public double ActiveThreshold { get; set; }
    public double FallbackThreshold { get; set; }
    public string ThresholdSource { get; set; } = "Configured";
    public int ThresholdSampleCount { get; set; }
    public double ThresholdHitRate { get; set; }
    public double ThresholdPublishedPerWeek { get; set; }
    public double ThresholdBrierScore { get; set; }
    public DateTime? ThresholdLastUpdated { get; set; }
    public double? BetaBaselineBrierScore { get; set; }
    public double? BetaValidationBrierScore { get; set; }
    public double? BetaImprovement { get; set; }
    public bool BetaRecommended { get; set; }
    public int? BetaTrainingSampleCount { get; set; }
    public int? BetaValidationSampleCount { get; set; }
    public DateTime? BetaLastUpdated { get; set; }
}
