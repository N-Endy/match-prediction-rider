using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Infrastructure.Services;

public class ThresholdTuningService : IThresholdTuningService
{
    private const int EvaluationWindowDays = 60;
    private const double RecencyHalfLifeDays = 21.0;
    private const int MinimumTrainingSampleCount = 25;
    private const int MinimumValidationSampleCount = 15;
    private const int MinimumPublishedSampleCount = 20;
    private const int MinimumValidationPublishedSampleCount = 8;
    private const double MinimumPublishedPerWeek = 1.5;
    private const double MinimumValidationImprovement = 0.0025;
    private const double ThresholdStep = 0.01;
    private const double MinimumThreshold = 0.50;
    private const double MaximumThreshold = 0.90;
    private static readonly PredictionMarket[] ActiveThresholdMarkets =
    [
        PredictionMarket.BothTeamsScore,
        PredictionMarket.Over25Goals,
        PredictionMarket.Under25Goals,
        PredictionMarket.HomeWin,
        PredictionMarket.AwayWin
    ];

    private readonly ApplicationDbContext _dbContext;
    private readonly PredictionSettings _settings;
    private List<ThresholdProfile>? _profiles;

    public ThresholdTuningService(ApplicationDbContext dbContext, IOptions<PredictionSettings> options)
    {
        _dbContext = dbContext;
        _settings = options.Value;
    }

    // Loaded lazily so resolving the scoped service does not hit the database
    // on requests that never consult thresholds.
    private List<ThresholdProfile> Profiles => _profiles ??= _dbContext.ThresholdProfiles
        .AsNoTracking()
        .Where(profile => ActiveThresholdMarkets.Contains(profile.Market))
        .ToList();

    public double GetThreshold(PredictionMarket market, double fallbackThreshold)
    {
        return GetThresholdDecision(market, fallbackThreshold).Threshold;
    }

    public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold)
    {
        var profile = Profiles.FirstOrDefault(p => p.Market == market);
        if (profile == null || !profile.IsPromoted)
        {
            return new ThresholdDecision
            {
                Threshold = fallbackThreshold,
                ThresholdSource = "Configured"
            };
        }

        return new ThresholdDecision
        {
            Threshold = profile.Threshold,
            ThresholdSource = "Tuned"
        };
    }

    public async Task RebuildProfilesAsync()
    {
        var previousProfiles = Profiles.ToDictionary(profile => profile.Market);
        var cutoff = DateTime.UtcNow.AddDays(-EvaluationWindowDays);
        var forecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(forecast =>
                forecast.IsSettled &&
                forecast.OutcomeOccurred != null &&
                (forecast.SettledAt ?? forecast.CreatedAt) >= cutoff)
            .ToListAsync();
        var pointInTimeForecasts = PointInTimeBacktestingSelector.SelectForecasts(forecasts)
            .Where(forecast => ActiveThresholdMarkets.Contains(forecast.Market))
            .ToList();

        var rebuiltProfiles = new List<ThresholdProfile>();

        var markets = ActiveThresholdMarkets
            .Select(market => (Market: market, FallbackThreshold: ResolveFallbackThreshold(market)))
            .ToArray();

        foreach (var (market, fallbackThreshold) in markets)
        {
            var marketForecasts = pointInTimeForecasts
                .Where(forecast => forecast.Market == market)
                .OrderBy(forecast => forecast.SettledAt ?? forecast.CreatedAt)
                .ToList();

            if (marketForecasts.Count < MinimumTrainingSampleCount + MinimumValidationSampleCount)
            {
                continue;
            }

            var splitIndex = Math.Clamp(
                (int)Math.Round(marketForecasts.Count * 0.7),
                MinimumTrainingSampleCount,
                marketForecasts.Count - MinimumValidationSampleCount);

            if (splitIndex <= 0 || splitIndex >= marketForecasts.Count)
            {
                continue;
            }

            var trainingForecasts = marketForecasts.Take(splitIndex).ToList();
            var validationForecasts = marketForecasts.Skip(splitIndex).ToList();
            var trainingWindowDays = CalculateWindowDays(trainingForecasts);
            var validationWindowDays = CalculateWindowDays(validationForecasts);

            var trainingCandidates = BuildCandidates(trainingForecasts, trainingWindowDays).ToList();
            if (trainingCandidates.Count == 0)
            {
                continue;
            }

            var trainingSelected = trainingCandidates
                .Where(candidate =>
                    candidate.SampleCount >= MinimumPublishedSampleCount &&
                    candidate.PublishedPerWeek >= MinimumPublishedPerWeek)
                .OrderByDescending(candidate => candidate.ObjectiveScore)
                .ThenBy(candidate => candidate.BrierScore)
                .ThenByDescending(candidate => candidate.SampleCount)
                .FirstOrDefault();

            trainingSelected ??= trainingCandidates
                .Where(candidate => candidate.SampleCount > 0)
                .OrderBy(candidate => Math.Abs(candidate.Threshold - fallbackThreshold))
                .ThenByDescending(candidate => candidate.SampleCount)
                .First();

            var validationSelected = EvaluateThreshold(validationForecasts, trainingSelected.Threshold, validationWindowDays);
            var baselineValidation = EvaluateThreshold(validationForecasts, fallbackThreshold, validationWindowDays);
            var improvement = CalculateImprovement(validationSelected, baselineValidation);
            var isPromoted =
                validationSelected != null &&
                validationSelected.SampleCount >= Math.Min(MinimumValidationPublishedSampleCount, validationForecasts.Count) &&
                validationSelected.PublishedPerWeek >= Math.Min(MinimumPublishedPerWeek, validationWindowDays / 7.0) &&
                Math.Abs(trainingSelected.Threshold - fallbackThreshold) > 0.0001 &&
                IsSignificantThresholdPromotion(validationForecasts, fallbackThreshold, trainingSelected.Threshold);

            var activeValidation = isPromoted ? validationSelected : baselineValidation;

            rebuiltProfiles.Add(new ThresholdProfile
            {
                Market = market,
                BaselineThreshold = fallbackThreshold,
                Threshold = trainingSelected.Threshold,
                SampleCount = activeValidation?.SampleCount ?? 0,
                HitRate = activeValidation?.HitRate ?? 0.0,
                PublishedPerWeek = activeValidation?.PublishedPerWeek ?? 0.0,
                AverageCalibratedProbability = activeValidation?.AverageCalibratedProbability ?? 0.0,
                ObservedFrequency = activeValidation?.ObservedFrequency ?? 0.0,
                BrierScore = activeValidation?.BrierScore ?? 0.0,
                TrainingSampleCount = trainingForecasts.Count,
                ValidationSampleCount = validationForecasts.Count,
                BaselineHitRate = baselineValidation?.HitRate ?? 0.0,
                BaselineBrierScore = baselineValidation?.BrierScore ?? 0.0,
                Improvement = improvement,
                IsPromoted = isPromoted,
                LastUpdated = DateTime.UtcNow
            });
        }

        var promotionHistory = BuildPromotionHistory(previousProfiles, rebuiltProfiles);

        try
        {
            await _dbContext.ThresholdProfiles.ExecuteDeleteAsync();
        }
        catch (InvalidOperationException)
        {
            var existingProfiles = await _dbContext.ThresholdProfiles.ToListAsync();
            _dbContext.ThresholdProfiles.RemoveRange(existingProfiles);
        }

        await _dbContext.ThresholdProfiles.AddRangeAsync(rebuiltProfiles);
        if (promotionHistory.Count > 0)
        {
            await _dbContext.PromotionHistories.AddRangeAsync(promotionHistory);
        }
        await _dbContext.SaveChangesAsync();

        _profiles = rebuiltProfiles;
    }

    private static bool IsSignificantThresholdPromotion(
        IReadOnlyList<ForecastObservation> validationForecasts,
        double fallbackThreshold,
        double candidateThreshold)
    {
        if (validationForecasts.Count < MinimumValidationSampleCount)
        {
            return false;
        }

        var baselineSamples = validationForecasts
            .Where(forecast => forecast.CalibratedProbability >= fallbackThreshold)
            .Select(forecast => (
                Predicted: forecast.CalibratedProbability,
                Outcome: forecast.OutcomeOccurred == true,
                Weight: CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt)))
            .ToList();
        var candidateSamples = validationForecasts
            .Where(forecast => forecast.CalibratedProbability >= candidateThreshold)
            .Select(forecast => (
                Predicted: forecast.CalibratedProbability,
                Outcome: forecast.OutcomeOccurred == true,
                Weight: CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt)))
            .ToList();

        if (candidateSamples.Count < MinimumValidationPublishedSampleCount)
        {
            return false;
        }

        var baselineBrier = PromotionStatistics.WeightedBrier(baselineSamples, static probability => probability);
        var candidateBrier = PromotionStatistics.WeightedBrier(candidateSamples, static probability => probability);
        if (baselineBrier - candidateBrier <= MinimumValidationImprovement || candidateBrier >= baselineBrier)
        {
            return false;
        }

        const int bootstrapIterations = 200;
        var random = new Random(validationForecasts.Count * 43 + (int)(baselineBrier * 10_000));
        var bootstrapImprovements = new List<double>(bootstrapIterations);
        for (var iteration = 0; iteration < bootstrapIterations; iteration++)
        {
            var resampled = new List<ForecastObservation>(validationForecasts.Count);
            for (var index = 0; index < validationForecasts.Count; index++)
            {
                resampled.Add(validationForecasts[random.Next(validationForecasts.Count)]);
            }

            var resampledBaseline = EvaluateThreshold(resampled, fallbackThreshold, validationForecasts.Count);
            var resampledCandidate = EvaluateThreshold(resampled, candidateThreshold, validationForecasts.Count);
            bootstrapImprovements.Add(CalculateImprovement(resampledCandidate, resampledBaseline));
        }

        bootstrapImprovements.Sort();
        var lowerBoundIndex = (int)Math.Floor(bootstrapIterations * 0.05);
        return bootstrapImprovements[lowerBoundIndex] > MinimumValidationImprovement;
    }

    private static IEnumerable<ThresholdCandidate> BuildCandidates(
        IReadOnlyCollection<ForecastObservation> marketForecasts,
        double totalWindowDays)
    {
        for (var threshold = MinimumThreshold; threshold <= MaximumThreshold + 0.000001; threshold += ThresholdStep)
        {
            var roundedThreshold = Math.Round(threshold, 2);
            var candidate = EvaluateThreshold(marketForecasts, roundedThreshold, totalWindowDays);
            if (candidate != null)
            {
                yield return candidate;
            }
        }
    }

    private static ThresholdCandidate? EvaluateThreshold(
        IReadOnlyCollection<ForecastObservation> forecasts,
        double threshold,
        double totalWindowDays)
    {
        var published = forecasts
            .Where(forecast => forecast.CalibratedProbability >= threshold)
            .ToList();

        if (published.Count == 0)
        {
            return null;
        }

        var weightedCount = published.Sum(forecast => CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt));
        var weightedHits = published.Sum(forecast => (forecast.OutcomeOccurred == true ? 1.0 : 0.0) * CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt));
        var weightedProbability = published.Sum(forecast => forecast.CalibratedProbability * CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt));
        var weightedBrier = published.Sum(forecast =>
            Math.Pow(forecast.CalibratedProbability - (forecast.OutcomeOccurred == true ? 1.0 : 0.0), 2) *
            CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt));
        var weightedLogLoss = published.Sum(forecast =>
            BinaryLogLoss(forecast.CalibratedProbability, forecast.OutcomeOccurred == true) *
            CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt));

        var totalWeeks = Math.Max(totalWindowDays / 7.0, 1.0);
        var hitRate = weightedCount > 0 ? weightedHits / weightedCount : 0.0;
        var averageProbability = weightedCount > 0 ? weightedProbability / weightedCount : 0.0;
        var brierScore = weightedCount > 0 ? weightedBrier / weightedCount : 0.0;
        var logLoss = weightedCount > 0 ? weightedLogLoss / weightedCount : 0.0;
        var publishedPerWeek = weightedCount / totalWeeks;

        // Proper-scoring objective: minimize log-loss on the published slate. Volume is
        // enforced as a hard constraint (MinimumPublishedSampleCount/MinimumPublishedPerWeek)
        // rather than optimizing hit rate, which previously just pushed thresholds toward
        // the few near-certain picks.
        var objectiveScore = -logLoss;

        return new ThresholdCandidate
        {
            Threshold = threshold,
            SampleCount = published.Count,
            WeightedSampleCount = weightedCount,
            HitRate = hitRate,
            PublishedPerWeek = publishedPerWeek,
            AverageCalibratedProbability = averageProbability,
            ObservedFrequency = hitRate,
            BrierScore = brierScore,
            LogLoss = logLoss,
            ObjectiveScore = objectiveScore
        };
    }

    private static double CalculateImprovement(ThresholdCandidate? candidate, ThresholdCandidate? baseline)
    {
        if (candidate == null)
        {
            return double.NegativeInfinity;
        }

        if (baseline == null)
        {
            return candidate.ObjectiveScore;
        }

        return candidate.ObjectiveScore - baseline.ObjectiveScore;
    }

    private static double CalculateWindowDays(IReadOnlyList<ForecastObservation> forecasts)
    {
        if (forecasts.Count <= 1)
        {
            return 7.0;
        }

        var minDate = forecasts.Min(forecast => forecast.SettledAt ?? forecast.CreatedAt);
        var maxDate = forecasts.Max(forecast => forecast.SettledAt ?? forecast.CreatedAt);
        return Math.Max((maxDate - minDate).TotalDays + 1.0, 7.0);
    }

    private List<PromotionHistory> BuildPromotionHistory(
        IReadOnlyDictionary<PredictionMarket, ThresholdProfile> previousProfiles,
        IReadOnlyCollection<ThresholdProfile> rebuiltProfiles)
    {
        var history = new List<PromotionHistory>();
        var rebuiltLookup = rebuiltProfiles.ToDictionary(profile => profile.Market);

        var markets = ActiveThresholdMarkets
            .Select(market => (Market: market, FallbackThreshold: ResolveFallbackThreshold(market)))
            .ToArray();

        foreach (var (market, fallbackThreshold) in markets)
        {
            var previousProfile = previousProfiles.TryGetValue(market, out var existingProfile)
                ? existingProfile
                : null;
            var nextProfile = rebuiltLookup.TryGetValue(market, out var rebuiltProfile)
                ? rebuiltProfile
                : null;

            var previousSource = previousProfile?.IsPromoted == true ? "Tuned" : "Configured";
            var nextSource = nextProfile?.IsPromoted == true ? "Tuned" : "Configured";
            var previousThreshold = previousProfile?.IsPromoted == true
                ? previousProfile.Threshold
                : fallbackThreshold;
            var nextThreshold = nextProfile?.IsPromoted == true
                ? nextProfile.Threshold
                : fallbackThreshold;

            if (string.Equals(previousSource, nextSource, StringComparison.Ordinal) &&
                Math.Abs(previousThreshold - nextThreshold) < 0.0001)
            {
                continue;
            }

            history.Add(new PromotionHistory
            {
                Market = market,
                ChangeType = "Threshold",
                PreviousValue = previousSource,
                NewValue = nextSource,
                PreviousNumericValue = previousThreshold,
                NewNumericValue = nextThreshold,
                BaselineScore = nextProfile?.BaselineBrierScore,
                CandidateScore = nextProfile?.BrierScore,
                Improvement = nextProfile?.Improvement,
                EffectiveAt = DateTime.UtcNow
            });
        }

        return history;
    }

    private double ResolveFallbackThreshold(PredictionMarket market)
    {
        return _settings.ResolveFallbackThreshold(market);
    }

    private sealed class ThresholdCandidate
    {
        public double Threshold { get; init; }
        public int SampleCount { get; init; }
        public double WeightedSampleCount { get; init; }
        public double HitRate { get; init; }
        public double PublishedPerWeek { get; init; }
        public double AverageCalibratedProbability { get; init; }
        public double ObservedFrequency { get; init; }
        public double BrierScore { get; init; }
        public double LogLoss { get; init; }
        public double ObjectiveScore { get; init; }
    }

    private static double CalculateRecencyWeight(DateTime timestampUtc)
    {
        return RecencyWeighting.CalculateWeight(timestampUtc, RecencyHalfLifeDays);
    }

    private static double BinaryLogLoss(double probability, bool outcome)
    {
        var clamped = Math.Clamp(probability, 1e-6, 1.0 - 1e-6);
        return outcome ? -Math.Log(clamped) : -Math.Log(1.0 - clamped);
    }
}
