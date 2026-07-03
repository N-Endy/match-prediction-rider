using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

public class ProbabilityCorrectionService : IProbabilityCorrectionService
{
    private const int MinimumSampleCount = 60;
    private const int MinimumValidationCount = 20;
    private const double MinimumImprovement = 0.0015;
    private const int RebuildWindowDays = 120;
    private const double RecencyHalfLifeDays = 30.0;
    private readonly ApplicationDbContext _dbContext;
    private List<MetaModelProfile>? _profiles;

    public ProbabilityCorrectionService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Loaded lazily so resolving the scoped service does not hit the database
    // on requests that never apply corrections.
    private List<MetaModelProfile> Profiles => _profiles ??= _dbContext.MetaModelProfiles.AsNoTracking().ToList();

    public double ApplyCorrection(PredictionMarket market, double rawProbability)
    {
        var clamped = Math.Clamp(rawProbability, 1e-6, 1.0 - 1e-6);
        var profile = Profiles.FirstOrDefault(item => item.Market == market && item.IsPromoted);
        if (profile == null)
        {
            return clamped;
        }

        var logit = Math.Log(clamped / (1.0 - clamped));
        var correctedLogit = profile.Intercept + (profile.Slope * logit);
        return 1.0 / (1.0 + Math.Exp(-correctedLogit));
    }

    public async Task RebuildProfilesAsync()
    {
        var settled = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(item =>
                item.IsSettled &&
                item.OutcomeOccurred != null &&
                (item.SettledAt ?? item.CreatedAt) >= DateTime.UtcNow.AddDays(-RebuildWindowDays))
            .ToListAsync();

        // Deduplicate regeneration revisions so each fixture-market contributes one
        // point-in-time observation, mirroring the calibration rebuild.
        var pointInTimeSettled = PointInTimeBacktestingSelector.SelectForecasts(settled)
            .OrderBy(item => item.SettledAt ?? item.CreatedAt)
            .ToList();

        var rebuilt = new List<MetaModelProfile>();
        foreach (var group in pointInTimeSettled.GroupBy(item => item.Market))
        {
            var observations = group
                .Select(item => (
                    Probability: item.RawProbability,
                    Outcome: item.OutcomeOccurred == true,
                    Weight: CalculateRecencyWeight(item.SettledAt ?? item.CreatedAt)))
                .ToList();
            if (observations.Count < MinimumSampleCount)
            {
                continue;
            }

            var splitIndex = Math.Clamp((int)Math.Round(observations.Count * 0.7), MinimumSampleCount - MinimumValidationCount, observations.Count - MinimumValidationCount);
            var training = observations.Take(splitIndex).ToList();
            var validation = observations.Skip(splitIndex).ToList();
            if (validation.Count < MinimumValidationCount)
            {
                continue;
            }

            var fitted = FitLogitCorrection(training);
            var baselineBrier = WeightedBrier(validation, item => item.Probability);
            var candidateBrier = WeightedBrier(validation, item =>
                ApplyLogitCorrection(item.Probability, fitted.Intercept, fitted.Slope));
            var improvement = baselineBrier - candidateBrier;

            rebuilt.Add(new MetaModelProfile
            {
                Market = group.Key,
                Intercept = fitted.Intercept,
                Slope = fitted.Slope,
                TrainingSampleCount = training.Count,
                ValidationSampleCount = validation.Count,
                BaselineBrierScore = baselineBrier,
                CandidateBrierScore = candidateBrier,
                Improvement = improvement,
                IsPromoted = improvement > MinimumImprovement && candidateBrier < baselineBrier,
                LastUpdated = DateTime.UtcNow
            });
        }

        try
        {
            await _dbContext.MetaModelProfiles.ExecuteDeleteAsync();
        }
        catch (InvalidOperationException)
        {
            var existing = await _dbContext.MetaModelProfiles.ToListAsync();
            _dbContext.MetaModelProfiles.RemoveRange(existing);
        }

        await _dbContext.MetaModelProfiles.AddRangeAsync(rebuilt);
        await _dbContext.SaveChangesAsync();
        _profiles = rebuilt;
    }

    private static (double Intercept, double Slope) FitLogitCorrection(
        IReadOnlyCollection<(double Probability, bool Outcome, double Weight)> training)
    {
        var intercept = 0.0;
        var slope = 1.0;
        const double learningRate = 0.05;
        var totalWeight = Math.Max(training.Sum(item => item.Weight), 1e-6);

        for (var i = 0; i < 250; i++)
        {
            var gradIntercept = 0.0;
            var gradSlope = 0.0;
            foreach (var (probability, outcome, weight) in training)
            {
                var p = Math.Clamp(probability, 1e-6, 1.0 - 1e-6);
                var x = Math.Log(p / (1.0 - p));
                var y = outcome ? 1.0 : 0.0;
                var prediction = 1.0 / (1.0 + Math.Exp(-(intercept + (slope * x))));
                var error = prediction - y;
                gradIntercept += error * weight;
                gradSlope += error * x * weight;
            }

            intercept -= learningRate * gradIntercept / totalWeight;
            slope -= learningRate * gradSlope / totalWeight;
        }

        return (intercept, Math.Clamp(slope, 0.3, 1.8));
    }

    private static double WeightedBrier(
        IReadOnlyCollection<(double Probability, bool Outcome, double Weight)> samples,
        Func<(double Probability, bool Outcome, double Weight), double> probabilitySelector)
    {
        return PromotionStatistics.WeightedBrier(samples
            .Select(item => (probabilitySelector(item), item.Outcome, item.Weight))
            .ToList());
    }

    private static double ApplyLogitCorrection(double rawProbability, double intercept, double slope)
    {
        var p = Math.Clamp(rawProbability, 1e-6, 1.0 - 1e-6);
        var logit = Math.Log(p / (1.0 - p));
        return 1.0 / (1.0 + Math.Exp(-(intercept + (slope * logit))));
    }

    private static double CalculateRecencyWeight(DateTime timestampUtc)
    {
        return RecencyWeighting.CalculateWeight(timestampUtc, RecencyHalfLifeDays);
    }
}
