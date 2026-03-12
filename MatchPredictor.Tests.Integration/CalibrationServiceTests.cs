using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class CalibrationServiceTests
{
    [Fact]
    public async Task RebuildProfilesAsync_CreatesBetaProfile_WhenSettledHistoryIsSufficient()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var now = DateTime.UtcNow;

        for (var index = 0; index < 60; index++)
        {
            var occurred = index % 4 != 0;
            var rawProbability = occurred ? 0.74 : 0.38;
            var calibratedProbability = occurred ? 0.70 : 0.41;

            context.ForecastObservations.Add(new ForecastObservation
            {
                Date = now.AddDays(-index).ToString("dd-MM-yyyy"),
                Time = "18:00",
                League = "League",
                HomeTeam = $"Home{index}",
                AwayTeam = $"Away{index}",
                Market = PredictionMarket.Over25Goals,
                PredictedOutcome = "Over2.5Goals",
                RawProbability = rawProbability,
                CalibratedProbability = calibratedProbability,
                OutcomeOccurred = occurred,
                IsSettled = true,
                CreatedAt = now.AddDays(-index),
                SettledAt = now.AddDays(-index)
            });
        }

        await context.SaveChangesAsync();

        var service = new CalibrationService(context);

        await service.RebuildProfilesAsync();

        var bucketProfiles = await context.MarketCalibrationProfiles
            .Where(profile => profile.Market == PredictionMarket.Over25Goals)
            .ToListAsync();
        var betaProfile = await context.BetaCalibrationProfiles
            .SingleAsync(profile => profile.Market == PredictionMarket.Over25Goals);

        Assert.NotEmpty(bucketProfiles);
        Assert.True(betaProfile.TrainingSampleCount >= 20);
        Assert.True(betaProfile.ValidationSampleCount >= 15);
        Assert.True(betaProfile.Alpha > 0);
        Assert.True(betaProfile.Beta > 0);
    }

    [Fact]
    public async Task Calibrate_UsesPromotedBetaProfile_WhenOneExistsForTheMarket()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);

        context.MarketCalibrationProfiles.Add(new MarketCalibrationProfile
        {
            Market = PredictionMarket.BothTeamsScore,
            BucketStart = 0.60,
            BucketEnd = 0.65,
            ObservationCount = 40,
            SuccessCount = 24,
            CalibratedProbability = 0.62,
            LastUpdated = DateTime.UtcNow
        });

        context.BetaCalibrationProfiles.Add(new BetaCalibrationProfile
        {
            Market = PredictionMarket.BothTeamsScore,
            Alpha = 1.8,
            Beta = 0.9,
            Gamma = 0.1,
            TrainingSampleCount = 50,
            ValidationSampleCount = 20,
            BaselineBrierScore = 0.210,
            ValidationBrierScore = 0.195,
            Improvement = 0.015,
            IsRecommended = true,
            LastUpdated = DateTime.UtcNow
        });

        await context.SaveChangesAsync();

        var service = new CalibrationService(context);
        var rawProbability = 0.62;

        var calibrated = service.Calibrate(PredictionMarket.BothTeamsScore, rawProbability);
        var expected = 1.0 / (1.0 + Math.Exp(-((1.8 * Math.Log(rawProbability)) - (0.9 * Math.Log(1.0 - rawProbability)) + 0.1)));

        Assert.Equal(expected, calibrated, 6);
    }

    [Fact]
    public async Task RebuildProfilesAsync_RecordsPromotionHistory_WhenBetaDemotesToBucket()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        context.BetaCalibrationProfiles.Add(new BetaCalibrationProfile
        {
            Market = PredictionMarket.BothTeamsScore,
            Alpha = 1.4,
            Beta = 0.8,
            Gamma = 0.2,
            TrainingSampleCount = 50,
            ValidationSampleCount = 20,
            BaselineBrierScore = 0.220,
            ValidationBrierScore = 0.201,
            Improvement = 0.019,
            IsRecommended = true,
            LastUpdated = DateTime.UtcNow
        });

        await context.SaveChangesAsync();

        var service = new CalibrationService(context);

        await service.RebuildProfilesAsync();

        var history = await context.PromotionHistories.SingleAsync();

        Assert.Equal(PredictionMarket.BothTeamsScore, history.Market);
        Assert.Equal("Calibrator", history.ChangeType);
        Assert.Equal("Beta", history.PreviousValue);
        Assert.Equal("Bucket", history.NewValue);
    }
}
