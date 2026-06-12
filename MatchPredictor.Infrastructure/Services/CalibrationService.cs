using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

public class CalibrationService : ICalibrationService
{
    private const double BucketSize = 0.05;
    private const int MinimumBetaSampleCount = 40;
    private const int RebuildWindowDays = 120;
    private const double RecencyHalfLifeDays = 30.0;
    private static readonly PredictionMarket[] ActiveCalibrationMarkets =
    [
        PredictionMarket.BothTeamsScore,
        PredictionMarket.Over25Goals,
        PredictionMarket.Under25Goals,
        PredictionMarket.HomeWin,
        PredictionMarket.AwayWin
    ];
    private readonly ApplicationDbContext _dbContext;
    private List<MarketCalibrationProfile>? _profiles;
    private List<BetaCalibrationProfile>? _betaProfiles;

    public CalibrationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Loaded lazily so resolving the scoped service does not hit the database
    // on requests that never calibrate.
    private List<MarketCalibrationProfile> Profiles => _profiles ??= _dbContext.MarketCalibrationProfiles
        .AsNoTracking()
        .Where(profile => ActiveCalibrationMarkets.Contains(profile.Market))
        .ToList();

    private List<BetaCalibrationProfile> BetaProfiles => _betaProfiles ??= _dbContext.BetaCalibrationProfiles
        .AsNoTracking()
        .Where(profile => ActiveCalibrationMarkets.Contains(profile.Market))
        .ToList();

    public double Calibrate(PredictionMarket market, double rawProbability)
    {
        return CalibrateWithDecision(market, rawProbability).Probability;
    }

    public CalibrationDecision CalibrateWithDecision(PredictionMarket market, double rawProbability)
    {
        rawProbability = Math.Clamp(rawProbability, 0.0, 1.0);

        var betaProfile = BetaProfiles.FirstOrDefault(profile => profile.Market == market && profile.IsRecommended);
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
            Probability = CalibrateWithBucket(rawProbability, Profiles.Where(profile => profile.Market == market)),
            CalibratorUsed = "Bucket"
        };
    }

    public async Task RebuildProfilesAsync()
    {
        var previousCalibratorByMarket = BetaProfiles
            .ToDictionary(
                profile => profile.Market,
                profile => profile.IsRecommended ? "Beta" : "Bucket");

        var settledForecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(p =>
                p.IsSettled &&
                p.OutcomeOccurred != null &&
                (p.SettledAt ?? p.CreatedAt) >= DateTime.UtcNow.AddDays(-RebuildWindowDays))
            .ToListAsync();
        var pointInTimeForecasts = PointInTimeBacktestingSelector.SelectForecasts(settledForecasts)
            .Where(forecast => ActiveCalibrationMarkets.Contains(forecast.Market))
            .ToList();

        var rebuiltProfiles = pointInTimeForecasts
            .GroupBy(x => new
            {
                x.Market,
                BucketStart = GetBucketStart(GetCalibrationInput(x))
            })
            .Select(group =>
            {
                var observationCount = group.Count();
                var successCount = group.Count(item => item.OutcomeOccurred == true);
                var weightedSuccesses = group.Sum(item => item.OutcomeOccurred == true ? CalculateRecencyWeight(item.SettledAt ?? item.CreatedAt) : 0.0);
                var weightedObservations = group.Sum(item => CalculateRecencyWeight(item.SettledAt ?? item.CreatedAt));
                var empiricalBucketProbability = (weightedSuccesses + 1.0) / (weightedObservations + 2.0);
                var weight = Math.Min(weightedObservations / 20.0, 1.0);
                var averageInputProbability = group.Average(GetCalibrationInput);

                return new MarketCalibrationProfile
                {
                    Market = group.Key.Market,
                    BucketStart = group.Key.BucketStart,
                    BucketEnd = Math.Min(group.Key.BucketStart + BucketSize, 1.0),
                    ObservationCount = observationCount,
                    SuccessCount = successCount,
                    CalibratedProbability = Math.Clamp(
                        averageInputProbability + (weight * (empiricalBucketProbability - averageInputProbability)),
                        0.0,
                        1.0),
                    LastUpdated = DateTime.UtcNow
                };
            })
            .ToList();

        var betaProfiles = BuildBetaCalibrationProfiles(pointInTimeForecasts);
        var promotionHistory = BuildPromotionHistory(previousCalibratorByMarket, betaProfiles);

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
        if (promotionHistory.Count > 0)
        {
            await _dbContext.PromotionHistories.AddRangeAsync(promotionHistory);
        }
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

    private static List<PromotionHistory> BuildPromotionHistory(
        IReadOnlyDictionary<PredictionMarket, string> previousCalibratorByMarket,
        IReadOnlyCollection<BetaCalibrationProfile> betaProfiles)
    {
        var history = new List<PromotionHistory>();
        var nextCalibratorByMarket = betaProfiles
            .ToDictionary(
                profile => profile.Market,
                profile => profile.IsRecommended ? "Beta" : "Bucket");

        var markets = previousCalibratorByMarket.Keys
            .Union(nextCalibratorByMarket.Keys)
            .Where(market => ActiveCalibrationMarkets.Contains(market))
            .Distinct()
            .ToList();

        foreach (var market in markets)
        {
            var previous = previousCalibratorByMarket.TryGetValue(market, out var previousState)
                ? previousState
                : "Bucket";
            var next = nextCalibratorByMarket.TryGetValue(market, out var nextState)
                ? nextState
                : "Bucket";

            if (string.Equals(previous, next, StringComparison.Ordinal))
            {
                continue;
            }

            var profile = betaProfiles.FirstOrDefault(item => item.Market == market);
            history.Add(new PromotionHistory
            {
                Market = market,
                ChangeType = "Calibrator",
                PreviousValue = previous,
                NewValue = next,
                BaselineScore = profile?.BaselineBrierScore,
                CandidateScore = profile?.ValidationBrierScore,
                Improvement = profile?.Improvement,
                EffectiveAt = DateTime.UtcNow
            });
        }

        return history;
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

        // Calibration receives meta-corrected probabilities at inference time, so it must
        // also be trained on the corrected values (falling back to raw for legacy rows).
        var training = forecasts.Take(splitIndex)
            .Select(forecast => (
                RawProbability: GetCalibrationInput(forecast),
                Outcome: forecast.OutcomeOccurred == true,
                Weight: CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt)))
            .ToList();
        var validation = forecasts.Skip(splitIndex)
            .Select(forecast => (
                RawProbability: GetCalibrationInput(forecast),
                Outcome: forecast.OutcomeOccurred == true,
                Weight: CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt)))
            .ToList();

        if (training.Count < 20 || validation.Count < 15)
        {
            return null;
        }

        var trainingBucketProfiles = BuildTrainingBucketProfiles(training);
        var bestValidationParameters = FitBetaCalibration(training);
        var validationWeight = validation.Sum(item => item.Weight);
        var baselineBrier = validationWeight <= 0
            ? 0.0
            : validation.Sum(item => SquaredError(
                CalibrateWithBucket(item.RawProbability, trainingBucketProfiles),
                item.Outcome) * item.Weight) / validationWeight;
        var betaBrier = validationWeight <= 0
            ? 0.0
            : validation.Sum(item => SquaredError(
                ApplyBetaCalibration(item.RawProbability, bestValidationParameters.alpha, bestValidationParameters.beta, bestValidationParameters.gamma),
                item.Outcome) * item.Weight) / validationWeight;
        var improvement = baselineBrier - betaBrier;
        var shouldPromote = improvement > 0.0025 && betaBrier < baselineBrier;
        var deployedParameters = FitBetaCalibration(forecasts
            .Select(forecast => (
                RawProbability: GetCalibrationInput(forecast),
                Outcome: forecast.OutcomeOccurred == true,
                Weight: CalculateRecencyWeight(forecast.SettledAt ?? forecast.CreatedAt)))
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
        IReadOnlyCollection<(double RawProbability, bool Outcome, double Weight)> training)
    {
        return training
            .GroupBy(item => GetBucketStart(item.RawProbability))
            .ToDictionary(
                group => group.Key,
                group => new BucketCalibrationStats(
                    group.Sum(item => item.Weight),
                    group.Sum(item => item.Outcome ? item.Weight : 0.0)));
    }

    private static double CalibrateWithBucket(
        double rawProbability,
        IEnumerable<MarketCalibrationProfile> profiles)
    {
        var bucketStart = GetBucketStart(rawProbability);
        var profile = profiles.FirstOrDefault(p => p.BucketStart == bucketStart);
        if (profile == null || profile.ObservationWeight <= 0)
        {
            return rawProbability;
        }

        var empiricalBucketProbability = (profile.SuccessWeight + 1.0) / (profile.ObservationWeight + 2.0);
        var weight = Math.Min(profile.ObservationWeight / 20.0, 1.0);
        return Math.Clamp(rawProbability + (weight * (empiricalBucketProbability - rawProbability)), 0.0, 1.0);
    }

    private static double CalibrateWithBucket(
        double rawProbability,
        IReadOnlyDictionary<double, BucketCalibrationStats> profiles)
    {
        var bucketStart = GetBucketStart(rawProbability);
        if (!profiles.TryGetValue(bucketStart, out var profile) || profile.ObservationWeight <= 0)
        {
            return rawProbability;
        }

        var empiricalBucketProbability = (profile.SuccessWeight + 1.0) / (profile.ObservationWeight + 2.0);
        var weight = Math.Min(profile.ObservationWeight / 20.0, 1.0);
        return Math.Clamp(rawProbability + (weight * (empiricalBucketProbability - rawProbability)), 0.0, 1.0);
    }

    private static (double alpha, double beta, double gamma) FitBetaCalibration(IReadOnlyList<(double RawProbability, bool Outcome, double Weight)> training)
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

    private static double ScoreBetaParameters(IReadOnlyList<(double RawProbability, bool Outcome, double Weight)> observations, double alpha, double beta, double gamma)
    {
        var weightedError = observations.Sum(item =>
            SquaredError(ApplyBetaCalibration(item.RawProbability, alpha, beta, gamma), item.Outcome) * item.Weight);
        var totalWeight = observations.Sum(item => item.Weight);
        return totalWeight <= 0 ? 0.0 : weightedError / totalWeight;
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

    private static double CalculateRecencyWeight(DateTime timestampUtc)
    {
        var ageDays = Math.Max((DateTime.UtcNow - timestampUtc).TotalDays, 0.0);
        return Math.Pow(0.5, ageDays / RecencyHalfLifeDays);
    }

    /// <summary>
    /// The probability that calibration actually receives at inference time is the
    /// meta-corrected one. Legacy rows (before CorrectedProbability existed) fall back to raw.
    /// </summary>
    private static double GetCalibrationInput(ForecastObservation forecast)
    {
        var input = forecast.CorrectedProbability > 0
            ? forecast.CorrectedProbability
            : forecast.RawProbability;
        return Math.Clamp(input, 0.0, 1.0);
    }

    private sealed record BucketCalibrationStats(double ObservationWeight, double SuccessWeight);
}
