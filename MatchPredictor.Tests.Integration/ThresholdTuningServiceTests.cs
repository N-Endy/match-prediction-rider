using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ThresholdTuningServiceTests
{
    [Fact]
    public async Task RebuildProfilesAsync_PromotesThreshold_WhenValidationBeatsConfiguredBaseline()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var now = DateTime.UtcNow;

        SeedForecasts(
            context,
            now,
            PredictionMarket.BothTeamsScore,
            30,
            0.74,
            true,
            "High");
        SeedForecasts(
            context,
            now.AddMinutes(-1),
            PredictionMarket.BothTeamsScore,
            30,
            0.56,
            false,
            "Mid");
        SeedForecasts(
            context,
            now.AddMinutes(-2),
            PredictionMarket.BothTeamsScore,
            30,
            0.44,
            false,
            "Low");

        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.RebuildProfilesAsync();

        var profile = await context.ThresholdProfiles.SingleAsync(p => p.Market == PredictionMarket.BothTeamsScore);
        var decision = service.GetThresholdDecision(PredictionMarket.BothTeamsScore, 0.55);

        Assert.True(profile.IsPromoted);
        Assert.True(profile.Threshold > 0.56);
        Assert.True(profile.Improvement > 0);
        Assert.Equal("Tuned", decision.ThresholdSource);
        Assert.Equal(profile.Threshold, decision.Threshold, 3);
    }

    [Fact]
    public async Task RebuildProfilesAsync_IgnoresLegacyDrawMarket_WhenRebuildingActiveThresholds()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var now = DateTime.UtcNow;

        context.ThresholdProfiles.Add(new ThresholdProfile
        {
            Market = PredictionMarket.Draw,
            BaselineThreshold = 0.30,
            Threshold = 0.42,
            SampleCount = 30,
            HitRate = 0.55,
            PublishedPerWeek = 2.0,
            AverageCalibratedProbability = 0.44,
            ObservedFrequency = 0.55,
            BrierScore = 0.210,
            TrainingSampleCount = 30,
            ValidationSampleCount = 15,
            BaselineHitRate = 0.50,
            BaselineBrierScore = 0.220,
            Improvement = 0.010,
            IsPromoted = true,
            LastUpdated = now
        });

        SeedForecasts(
            context,
            now,
            PredictionMarket.Draw,
            45,
            0.60,
            true,
            "BalancedWin");
        SeedForecasts(
            context,
            now.AddMinutes(-1),
            PredictionMarket.Draw,
            45,
            0.60,
            false,
            "BalancedLoss");

        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.RebuildProfilesAsync();

        var decision = service.GetThresholdDecision(PredictionMarket.Draw, 0.54);
        var drawProfiles = await context.ThresholdProfiles
            .Where(p => p.Market == PredictionMarket.Draw)
            .ToListAsync();
        var drawHistory = await context.PromotionHistories
            .Where(history => history.Market == PredictionMarket.Draw)
            .ToListAsync();

        Assert.Empty(drawProfiles);
        Assert.Empty(drawHistory);
        Assert.Equal(0.54, decision.Threshold, 3);
        Assert.Equal("Configured", decision.ThresholdSource);
    }

    [Fact]
    public async Task RebuildProfilesAsync_RecordsPromotionHistory_WhenThresholdFallsBackToConfigured()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        context.ThresholdProfiles.Add(new ThresholdProfile
        {
            Market = PredictionMarket.Over25Goals,
            BaselineThreshold = 0.58,
            Threshold = 0.64,
            SampleCount = 24,
            HitRate = 0.72,
            PublishedPerWeek = 2.5,
            AverageCalibratedProbability = 0.69,
            ObservedFrequency = 0.72,
            BrierScore = 0.180,
            TrainingSampleCount = 50,
            ValidationSampleCount = 18,
            BaselineHitRate = 0.62,
            BaselineBrierScore = 0.220,
            Improvement = 0.015,
            IsPromoted = true,
            LastUpdated = DateTime.UtcNow
        });

        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.RebuildProfilesAsync();

        var history = await context.PromotionHistories.SingleAsync();

        Assert.Equal(PredictionMarket.Over25Goals, history.Market);
        Assert.Equal("Threshold", history.ChangeType);
        Assert.Equal("Tuned", history.PreviousValue);
        Assert.Equal("Configured", history.NewValue);
        Assert.Equal(0.64, history.PreviousNumericValue.GetValueOrDefault(), 3);
        Assert.Equal(0.58, history.NewNumericValue.GetValueOrDefault(), 3);
    }

    [Fact]
    public async Task RebuildProfilesAsync_UsesPointInTimeForecastRevisionPerFixture()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTime.UtcNow.Date.AddHours(18);

        for (var index = 0; index < 50; index++)
        {
            var fixtureKickoff = kickoff.AddDays(-index);
            context.ForecastObservations.Add(new ForecastObservation
            {
                Date = fixtureKickoff.ToString("dd-MM-yyyy"),
                Time = "18:00",
                MatchLocalDate = DateOnly.FromDateTime(fixtureKickoff),
                MatchLocalTime = new TimeOnly(18, 0),
                MatchDateTime = fixtureKickoff,
                FixtureKey = $"league|fixture-{index}",
                League = "League",
                HomeTeam = $"Home{index}",
                AwayTeam = $"Away{index}",
                Market = PredictionMarket.BothTeamsScore,
                PredictedOutcome = "BTTS",
                RawProbability = 0.70,
                CalibratedProbability = 0.72,
                OutcomeOccurred = true,
                IsSettled = true,
                RevisionNumber = 1,
                CreatedAt = fixtureKickoff.AddHours(-2),
                SettledAt = fixtureKickoff.AddHours(2)
            });
        }

        context.ForecastObservations.Add(new ForecastObservation
        {
            Date = kickoff.ToString("dd-MM-yyyy"),
            Time = "18:00",
            MatchLocalDate = DateOnly.FromDateTime(kickoff),
            MatchLocalTime = new TimeOnly(18, 0),
            MatchDateTime = kickoff,
            FixtureKey = "league|fixture-0",
            League = "League",
            HomeTeam = "Home0",
            AwayTeam = "Away0",
            Market = PredictionMarket.BothTeamsScore,
            PredictedOutcome = "BTTS",
            RawProbability = 0.22,
            CalibratedProbability = 0.24,
            OutcomeOccurred = false,
            IsSettled = true,
            RevisionNumber = 2,
            CreatedAt = kickoff.AddHours(1),
            SettledAt = kickoff.AddHours(2)
        });

        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.RebuildProfilesAsync();

        var profile = await context.ThresholdProfiles.SingleAsync(p => p.Market == PredictionMarket.BothTeamsScore);

        Assert.Equal(50, profile.TrainingSampleCount + profile.ValidationSampleCount);
    }

    private static ThresholdTuningService CreateService(ApplicationDbContext context)
    {
        return new ThresholdTuningService(
            context,
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                UnderTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }));
    }

    private static void SeedForecasts(
        ApplicationDbContext context,
        DateTime now,
        PredictionMarket market,
        int count,
        double calibratedProbability,
        bool occurred,
        string prefix)
    {
        for (var index = 0; index < count; index++)
        {
            var fixtureKickoff = now.AddDays(-index).Date.AddHours(18);
            context.ForecastObservations.Add(new ForecastObservation
            {
                Date = fixtureKickoff.ToString("dd-MM-yyyy"),
                Time = "18:00",
                MatchLocalDate = DateOnly.FromDateTime(fixtureKickoff),
                MatchLocalTime = new TimeOnly(18, 0),
                MatchDateTime = fixtureKickoff,
                FixtureKey = $"league|{prefix.ToLowerInvariant()}|{index}",
                League = "League",
                HomeTeam = $"{prefix}Home{index}",
                AwayTeam = $"{prefix}Away{index}",
                Market = market,
                PredictedOutcome = market switch
                {
                    PredictionMarket.BothTeamsScore => "BTTS",
                    PredictionMarket.Draw => "Draw",
                    _ => "Outcome"
                },
                RawProbability = Math.Max(0.05, calibratedProbability - 0.02),
                CalibratedProbability = calibratedProbability,
                OutcomeOccurred = occurred,
                IsSettled = true,
                CreatedAt = fixtureKickoff.AddHours(-2),
                SettledAt = fixtureKickoff.AddHours(2)
            });
        }
    }
}
