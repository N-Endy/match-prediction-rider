using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Infrastructure.Services;

public class ThresholdTuningService : IThresholdTuningService
{
    private const int EvaluationWindowDays = 60;
    private const int MinimumTrainingSampleCount = 25;
    private const int MinimumValidationSampleCount = 15;
    private const int MinimumPublishedSampleCount = 20;
    private const int MinimumValidationPublishedSampleCount = 8;
    private const double MinimumPublishedPerWeek = 1.5;
    private const double MinimumValidationImprovement = 0.0025;
    private const double ThresholdStep = 0.01;
    private const double MinimumThreshold = 0.50;
    private const double MaximumThreshold = 0.90;

    private readonly ApplicationDbContext _dbContext;
    private readonly PredictionSettings _settings;
    private List<ThresholdProfile> _profiles;

    public ThresholdTuningService(ApplicationDbContext dbContext, IOptions<PredictionSettings> options)
    {
        _dbContext = dbContext;
        _settings = options.Value;
        _profiles = _dbContext.ThresholdProfiles
            .AsNoTracking()
            .ToList();
    }

    public double GetThreshold(PredictionMarket market, double fallbackThreshold)
    {
        return GetThresholdDecision(market, fallbackThreshold).Threshold;
    }

    public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold)
    {
        var profile = _profiles.FirstOrDefault(p => p.Market == market);
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
        var cutoff = DateTime.UtcNow.AddDays(-EvaluationWindowDays);
        var forecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(forecast =>
                forecast.IsSettled &&
                forecast.OutcomeOccurred != null &&
                (forecast.SettledAt ?? forecast.CreatedAt) >= cutoff)
            .ToListAsync();

        var rebuiltProfiles = new List<ThresholdProfile>();

        var markets = new[]
        {
            (PredictionMarket.BothTeamsScore, _settings.BttsScoreThreshold),
            (PredictionMarket.Over25Goals, _settings.OverTwoGoalsStrongThreshold),
            (PredictionMarket.Draw, _settings.DrawStrongThreshold),
            (PredictionMarket.HomeWin, _settings.HomeWinStrong),
            (PredictionMarket.AwayWin, _settings.AwayWinStrong)
        };

        foreach (var (market, fallbackThreshold) in markets)
        {
            var marketForecasts = forecasts
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
                improvement > MinimumValidationImprovement;

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
        await _dbContext.SaveChangesAsync();

        _profiles = rebuiltProfiles;
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

        var totalWeeks = Math.Max(totalWindowDays / 7.0, 1.0);
        var hitRate = published.Average(forecast => forecast.OutcomeOccurred == true ? 1.0 : 0.0);
        var averageProbability = published.Average(forecast => forecast.CalibratedProbability);
        var brierScore = published.Average(forecast =>
            Math.Pow(forecast.CalibratedProbability - (forecast.OutcomeOccurred == true ? 1.0 : 0.0), 2));
        var publishedPerWeek = published.Count / totalWeeks;
        var calibrationGap = Math.Abs(averageProbability - hitRate);
        var objectiveScore = hitRate - (brierScore * 0.25) - (calibrationGap * 0.10);

        return new ThresholdCandidate
        {
            Threshold = threshold,
            SampleCount = published.Count,
            HitRate = hitRate,
            PublishedPerWeek = publishedPerWeek,
            AverageCalibratedProbability = averageProbability,
            ObservedFrequency = hitRate,
            BrierScore = brierScore,
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

    private sealed class ThresholdCandidate
    {
        public double Threshold { get; init; }
        public int SampleCount { get; init; }
        public double HitRate { get; init; }
        public double PublishedPerWeek { get; init; }
        public double AverageCalibratedProbability { get; init; }
        public double ObservedFrequency { get; init; }
        public double BrierScore { get; init; }
        public double ObjectiveScore { get; init; }
    }
}
