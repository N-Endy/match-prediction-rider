using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ProbabilityCorrectionServiceTests
{
    [Fact]
    public async Task RebuildProfilesAsync_AppliesRecencyWeightingDuringMetaModelTraining()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var now = DateTime.UtcNow;

        for (var index = 0; index < 50; index++)
        {
            context.ForecastObservations.Add(CreateForecast(
                now.AddDays(-90 + index),
                rawProbability: 0.72,
                outcomeOccurred: true,
                index));
        }

        for (var index = 0; index < 15; index++)
        {
            context.ForecastObservations.Add(CreateForecast(
                now.AddHours(-index),
                rawProbability: 0.72,
                outcomeOccurred: false,
                index + 100));
        }

        await context.SaveChangesAsync();

        var service = new ProbabilityCorrectionService(context);

        await service.RebuildProfilesAsync();

        var profile = await context.MetaModelProfiles
            .SingleAsync(item => item.Market == PredictionMarket.Over25Goals);

        Assert.True(profile.TrainingSampleCount >= 42);
        Assert.True(profile.ValidationSampleCount >= 20);
        Assert.True(profile.BaselineBrierScore >= 0);
        Assert.InRange(profile.Slope, 0.3, 1.8);
    }

    private static ForecastObservation CreateForecast(
        DateTime settledAt,
        double rawProbability,
        bool outcomeOccurred,
        int index)
    {
        return new ForecastObservation
        {
            Date = settledAt.ToString("dd-MM-yyyy"),
            Time = "18:00",
            MatchLocalDate = DateOnly.FromDateTime(settledAt),
            MatchLocalTime = new TimeOnly(18, 0),
            MatchDateTime = settledAt,
            FixtureKey = $"league|fixture-{index}",
            League = "League",
            HomeTeam = $"Home{index}",
            AwayTeam = $"Away{index}",
            Market = PredictionMarket.Over25Goals,
            PredictedOutcome = "Over2.5Goals",
            RawProbability = rawProbability,
            CalibratedProbability = rawProbability,
            OutcomeOccurred = outcomeOccurred,
            IsSettled = true,
            CreatedAt = settledAt.AddHours(-1),
            SettledAt = settledAt
        };
    }
}
