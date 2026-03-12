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
