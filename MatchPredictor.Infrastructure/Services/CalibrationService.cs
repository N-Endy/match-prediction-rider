using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

public class CalibrationService : ICalibrationService
{
    private const double BucketSize = 0.05;
    private readonly ApplicationDbContext _dbContext;
    private List<MarketCalibrationProfile> _profiles;

    public CalibrationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
        _profiles = _dbContext.MarketCalibrationProfiles
            .AsNoTracking()
            .ToList();
    }

    public double Calibrate(PredictionMarket market, double rawProbability)
    {
        rawProbability = Math.Clamp(rawProbability, 0.0, 1.0);

        var bucketStart = GetBucketStart(rawProbability);
        var profile = _profiles.FirstOrDefault(p => p.Market == market && p.BucketStart == bucketStart);
        if (profile == null || profile.ObservationCount <= 0)
            return rawProbability;

        var empiricalBucketProbability = (profile.SuccessCount + 1.0) / (profile.ObservationCount + 2.0);
        var weight = Math.Min(profile.ObservationCount / 20.0, 1.0);
        return Math.Clamp(rawProbability + (weight * (empiricalBucketProbability - rawProbability)), 0.0, 1.0);
    }

    public async Task RebuildProfilesAsync()
    {
        var finalizedPredictions = await _dbContext.Predictions
            .AsNoTracking()
            .Where(p =>
                !p.IsLive &&
                p.ActualOutcome != null &&
                p.RawConfidenceScore != null)
            .ToListAsync();

        var rebuiltProfiles = finalizedPredictions
            .Select(prediction =>
            {
                if (!PredictionMarketExtensions.TryFromCategory(prediction.PredictionCategory, out var market))
                    return null;

                var rawProbability = (double?)prediction.RawConfidenceScore;
                if (rawProbability is null)
                    return null;

                return new
                {
                    market,
                    RawProbability = Math.Clamp(rawProbability.Value, 0.0, 1.0),
                    IsSuccess = string.Equals(prediction.PredictedOutcome, prediction.ActualOutcome, StringComparison.Ordinal)
                };
            })
            .Where(x => x != null)
            .GroupBy(x => new
            {
                x!.market,
                BucketStart = GetBucketStart(x.RawProbability)
            })
            .Select(group =>
            {
                var observationCount = group.Count();
                var successCount = group.Count(item => item!.IsSuccess);
                var empiricalBucketProbability = (successCount + 1.0) / (observationCount + 2.0);
                var weight = Math.Min(observationCount / 20.0, 1.0);
                var averageRawProbability = group.Average(item => item!.RawProbability);

                return new MarketCalibrationProfile
                {
                    Market = group.Key.market,
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

        await _dbContext.MarketCalibrationProfiles.ExecuteDeleteAsync();
        await _dbContext.MarketCalibrationProfiles.AddRangeAsync(rebuiltProfiles);
        await _dbContext.SaveChangesAsync();

        _profiles = rebuiltProfiles;
    }

    private static double GetBucketStart(double probability)
    {
        var clamped = Math.Clamp(probability, 0.0, 0.999999);
        return Math.Floor(clamped / BucketSize) * BucketSize;
    }
}
