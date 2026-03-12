using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

public class CalibrationService : ICalibrationService
{
    private const double BucketSize = 0.05;
    private const int MinimumBetaSampleCount = 40;
    private readonly ApplicationDbContext _dbContext;
    private List<MarketCalibrationProfile> _profiles;
    private List<BetaCalibrationProfile> _betaProfiles;

    public CalibrationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
        _profiles = _dbContext.MarketCalibrationProfiles
            .AsNoTracking()
            .ToList();
        _betaProfiles = _dbContext.BetaCalibrationProfiles
            .AsNoTracking()
            .ToList();
    }

    public double Calibrate(PredictionMarket market, double rawProbability)
    {
        return CalibrateWithDecision(market, rawProbability).Probability;
    }

    public CalibrationDecision CalibrateWithDecision(PredictionMarket market, double rawProbability)
    {
        rawProbability = Math.Clamp(rawProbability, 0.0, 1.0);

        var betaProfile = _betaProfiles.FirstOrDefault(profile => profile.Market == market && profile.IsRecommended);
        if (betaProfile != null)
        {
            return new CalibrationDecision
            {
                Probability = ApplyBetaCalibration(rawProbability, betaProfile.Alpha, betaProfile.Beta, betaProfile.Gamma),
                CalibratorUsed = "Beta"
            };
        }

        return new CalibrationDecision
        {
            Probability = CalibrateWithBucket(rawProbability, _profiles.Where(profile => profile.Market == market)),
            CalibratorUsed = "Bucket"
        };
    }

    public async Task RebuildProfilesAsync()
    {
        var settledForecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(p =>
                p.IsSettled &&
                p.OutcomeOccurred != null)
            .ToListAsync();

        var rebuiltProfiles = settledForecasts
            .GroupBy(x => new
            {
                x.Market,
                BucketStart = GetBucketStart(x.RawProbability)
            })
            .Select(group =>
            {
                var observationCount = group.Count();
                var successCount = group.Count(item => item.OutcomeOccurred == true);
                var empiricalBucketProbability = (successCount + 1.0) / (observationCount + 2.0);
                var weight = Math.Min(observationCount / 20.0, 1.0);
                var averageRawProbability = group.Average(item => item.RawProbability);

                return new MarketCalibrationProfile
                {
                    Market = group.Key.Market,
                    BucketStart = group.Key.BucketStart,
                    BucketEnd = Math.Min(group.Key.BucketStart + BucketSize, 1.0),
                    ObservationCount = observationCount,
                    SuccessCount = successCount,
                    CalibratedProbability = Math.Clamp(
                        averageRawProbability + (weight * (empiricalBucketProbability - averageRawProbability)),
                        0.0,
                        1.0),
                    LastUpdated = DateTime.UtcNow
                };
            })
            .ToList();

        var betaProfiles = BuildBetaCalibrationProfiles(settledForecasts);

        try
        {
            await _dbContext.MarketCalibrationProfiles.ExecuteDeleteAsync();
        }
        catch (InvalidOperationException)
        {
            var existingProfiles = await _dbContext.MarketCalibrationProfiles.ToListAsync();
            _dbContext.MarketCalibrationProfiles.RemoveRange(existingProfiles);
        }

        await _dbContext.MarketCalibrationProfiles.AddRangeAsync(rebuiltProfiles);

        try
        {
            await _dbContext.BetaCalibrationProfiles.ExecuteDeleteAsync();
        }
        catch (InvalidOperationException)
        {
            var existingBetaProfiles = await _dbContext.BetaCalibrationProfiles.ToListAsync();
            _dbContext.BetaCalibrationProfiles.RemoveRange(existingBetaProfiles);
        }

        await _dbContext.BetaCalibrationProfiles.AddRangeAsync(betaProfiles);
        await _dbContext.SaveChangesAsync();

        _profiles = rebuiltProfiles;
        _betaProfiles = betaProfiles;
    }

    private static List<BetaCalibrationProfile> BuildBetaCalibrationProfiles(IReadOnlyCollection<ForecastObservation> settledForecasts)
    {
        return settledForecasts
            .GroupBy(forecast => forecast.Market)
            .Select(group => BuildBetaProfile(group.Key, group
                .OrderBy(forecast => forecast.SettledAt ?? forecast.CreatedAt)
                .ToList()))
            .Where(profile => profile != null)
            .Cast<BetaCalibrationProfile>()
            .ToList();
    }

    private static BetaCalibrationProfile? BuildBetaProfile(PredictionMarket market, IReadOnlyList<ForecastObservation> forecasts)
    {
        if (forecasts.Count < MinimumBetaSampleCount)
        {
            return null;
        }

        var splitIndex = Math.Clamp((int)Math.Round(forecasts.Count * 0.7), 20, forecasts.Count - 15);
        if (splitIndex <= 0 || splitIndex >= forecasts.Count)
        {
            return null;
        }

        var training = forecasts.Take(splitIndex)
            .Select(forecast => (forecast.RawProbability, Outcome: forecast.OutcomeOccurred == true))
            .ToList();
        var validation = forecasts.Skip(splitIndex)
            .Select(forecast => (
                RawProbability: forecast.RawProbability,
                Outcome: forecast.OutcomeOccurred == true))
            .ToList();

        if (training.Count < 20 || validation.Count < 15)
        {
            return null;
        }

        var trainingBucketProfiles = BuildTrainingBucketProfiles(training);
        var bestValidationParameters = FitBetaCalibration(training);
        var baselineBrier = validation.Average(item => SquaredError(
            CalibrateWithBucket(item.RawProbability, trainingBucketProfiles),
            item.Outcome));
        var betaBrier = validation.Average(item => SquaredError(
            ApplyBetaCalibration(item.RawProbability, bestValidationParameters.alpha, bestValidationParameters.beta, bestValidationParameters.gamma),
            item.Outcome));
        var improvement = baselineBrier - betaBrier;
        var shouldPromote = improvement > 0.0025 && betaBrier < baselineBrier;
        var deployedParameters = FitBetaCalibration(forecasts
            .Select(forecast => (forecast.RawProbability, Outcome: forecast.OutcomeOccurred == true))
            .ToList());

        return new BetaCalibrationProfile
        {
            Market = market,
            Alpha = deployedParameters.alpha,
            Beta = deployedParameters.beta,
            Gamma = deployedParameters.gamma,
            TrainingSampleCount = training.Count,
            ValidationSampleCount = validation.Count,
            BaselineBrierScore = baselineBrier,
            ValidationBrierScore = betaBrier,
            Improvement = improvement,
            IsRecommended = shouldPromote,
            LastUpdated = DateTime.UtcNow
        };
    }

    private static IReadOnlyDictionary<double, BucketCalibrationStats> BuildTrainingBucketProfiles(
        IReadOnlyCollection<(double RawProbability, bool Outcome)> training)
    {
        return training
            .GroupBy(item => GetBucketStart(item.RawProbability))
            .ToDictionary(
                group => group.Key,
                group => new BucketCalibrationStats(
                    group.Count(),
                    group.Count(item => item.Outcome)));
    }

    private static double CalibrateWithBucket(
        double rawProbability,
        IEnumerable<MarketCalibrationProfile> profiles)
    {
        var bucketStart = GetBucketStart(rawProbability);
        var profile = profiles.FirstOrDefault(p => p.BucketStart == bucketStart);
        if (profile == null || profile.ObservationCount <= 0)
        {
            return rawProbability;
        }

        var empiricalBucketProbability = (profile.SuccessCount + 1.0) / (profile.ObservationCount + 2.0);
        var weight = Math.Min(profile.ObservationCount / 20.0, 1.0);
        return Math.Clamp(rawProbability + (weight * (empiricalBucketProbability - rawProbability)), 0.0, 1.0);
    }

    private static double CalibrateWithBucket(
        double rawProbability,
        IReadOnlyDictionary<double, BucketCalibrationStats> profiles)
    {
        var bucketStart = GetBucketStart(rawProbability);
        if (!profiles.TryGetValue(bucketStart, out var profile) || profile.ObservationCount <= 0)
        {
            return rawProbability;
        }

        var empiricalBucketProbability = (profile.SuccessCount + 1.0) / (profile.ObservationCount + 2.0);
        var weight = Math.Min(profile.ObservationCount / 20.0, 1.0);
        return Math.Clamp(rawProbability + (weight * (empiricalBucketProbability - rawProbability)), 0.0, 1.0);
    }

    private static (double alpha, double beta, double gamma) FitBetaCalibration(IReadOnlyList<(double RawProbability, bool Outcome)> training)
    {
        var best = (alpha: 1.0, beta: 1.0, gamma: 0.0);
        var bestScore = ScoreBetaParameters(training, best.alpha, best.beta, best.gamma);

        for (var alpha = 0.5; alpha <= 2.0 + 0.0001; alpha += 0.25)
        {
            for (var beta = 0.5; beta <= 2.0 + 0.0001; beta += 0.25)
            {
                for (var gamma = -1.0; gamma <= 1.0 + 0.0001; gamma += 0.25)
                {
                    var score = ScoreBetaParameters(training, alpha, beta, gamma);
                    if (score < bestScore)
                    {
                        best = (Math.Round(alpha, 3), Math.Round(beta, 3), Math.Round(gamma, 3));
                        bestScore = score;
                    }
                }
            }
        }

        for (var alpha = Math.Max(0.1, best.alpha - 0.25); alpha <= best.alpha + 0.25 + 0.0001; alpha += 0.05)
        {
            for (var beta = Math.Max(0.1, best.beta - 0.25); beta <= best.beta + 0.25 + 0.0001; beta += 0.05)
            {
                for (var gamma = best.gamma - 0.25; gamma <= best.gamma + 0.25 + 0.0001; gamma += 0.05)
                {
                    var score = ScoreBetaParameters(training, alpha, beta, gamma);
                    if (score < bestScore)
                    {
                        best = (Math.Round(alpha, 3), Math.Round(beta, 3), Math.Round(gamma, 3));
                        bestScore = score;
                    }
                }
            }
        }

        return best;
    }

    private static double ScoreBetaParameters(IReadOnlyList<(double RawProbability, bool Outcome)> observations, double alpha, double beta, double gamma)
    {
        return observations.Average(item => SquaredError(ApplyBetaCalibration(item.RawProbability, alpha, beta, gamma), item.Outcome));
    }

    private static double ApplyBetaCalibration(double rawProbability, double alpha, double beta, double gamma)
    {
        var clamped = Math.Clamp(rawProbability, 1e-6, 1.0 - 1e-6);
        var logit = (alpha * Math.Log(clamped)) - (beta * Math.Log(1.0 - clamped)) + gamma;
        return 1.0 / (1.0 + Math.Exp(-logit));
    }

    private static double SquaredError(double probability, bool outcome)
    {
        return Math.Pow(probability - (outcome ? 1.0 : 0.0), 2);
    }

    private static double GetBucketStart(double probability)
    {
        var clamped = Math.Clamp(probability, 0.0, 0.999999);
        return Math.Floor(clamped / BucketSize) * BucketSize;
    }

    private sealed record BucketCalibrationStats(int ObservationCount, int SuccessCount);
}
