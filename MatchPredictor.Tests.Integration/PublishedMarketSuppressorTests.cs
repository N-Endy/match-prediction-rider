using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class PublishedMarketSuppressorTests
{
    [Fact]
    public async Task EvaluateAsync_MarksCategory_WhenFinishedSettledRoiIsBelowFloor()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        SeedSettledCategory(
            context,
            category: "BothTeamsScore",
            predictedOutcome: "BTTS",
            actualOutcome: "No BTTS",
            count: 22,
            isLive: false,
            idOffset: 1);

        await context.SaveChangesAsync();

        var suppressed = await PublishedMarketSuppressor.EvaluateAsync(
            context,
            new PredictionSettings
            {
                SuppressPublishMinSettledBets = 20,
                SuppressPublishBelowRoiPercent = -5.0
            });

        var decision = Assert.Single(suppressed);
        Assert.Equal("BothTeamsScore", decision.Key);
        Assert.True(decision.Value.Suppress);
        Assert.Equal(22, decision.Value.SettledCount);
        Assert.Contains("ROI", decision.Value.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_IgnoresLiveUnfinishedRows()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        SeedSettledCategory(
            context,
            category: "BothTeamsScore",
            predictedOutcome: "BTTS",
            actualOutcome: "No BTTS",
            count: 25,
            isLive: true,
            idOffset: 1);

        await context.SaveChangesAsync();

        var suppressed = await PublishedMarketSuppressor.EvaluateAsync(
            context,
            new PredictionSettings
            {
                SuppressPublishMinSettledBets = 20,
                SuppressPublishBelowRoiPercent = -5.0
            });

        Assert.Empty(suppressed);
    }

    private static void SeedSettledCategory(
        ApplicationDbContext context,
        string category,
        string predictedOutcome,
        string actualOutcome,
        int count,
        bool isLive,
        int idOffset)
    {
        var lookbackDate = DateTimeProvider.GetLocalDate().AddDays(-2);
        for (var index = 0; index < count; index++)
        {
            var id = idOffset + index;
            context.Predictions.Add(new Prediction
            {
                Id = id,
                Date = lookbackDate.ToString("dd-MM-yyyy"),
                Time = "18:00",
                MatchLocalDate = lookbackDate,
                MatchDateTime = DateTime.UtcNow.AddDays(-2).AddMinutes(index),
                League = "League",
                HomeTeam = $"Home{id}",
                AwayTeam = $"Away{id}",
                FixtureKey = $"hist-{category}-{id}",
                PredictionCategory = category,
                PredictedOutcome = predictedOutcome,
                ActualOutcome = actualOutcome,
                ActualScore = isLive ? "0:0" : "1:0",
                IsLive = isLive,
                WasPublished = true,
                IsCurrentRevision = true,
                ConfidenceScore = 0.60m,
                PredictionRunId = Guid.NewGuid()
            });
            context.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
            {
                PredictionId = id,
                SnapshotKind = PredictionOddsSnapshotKind.Publish,
                DecimalOdds = 2.0,
                Outcome = predictedOutcome,
                Market = category,
                ImpliedProbability = 0.5,
                CapturedAtUtc = DateTime.UtcNow.AddDays(-2)
            });
        }
    }
}
