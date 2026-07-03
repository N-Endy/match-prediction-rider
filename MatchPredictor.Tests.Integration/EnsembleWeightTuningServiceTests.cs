using System.Text.Json;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class EnsembleWeightTuningServiceTests
{
    [Fact]
    public async Task RebuildProfilesAsync_PromotesBookmakerHeavyWeights_WhenBookmakerSignalIsSharper()
    {
        await using var context = CreateContext();

        // Bookmaker signal equals the outcome-generating probability; the calculator
        // signal is anti-correlated so the incumbent ProductionDefault blend is clearly
        // suboptimal. Learned weights should shift toward the bookmaker and beat the
        // incumbent on the holdout.
        SeedForecasts(
            context,
            count: 400,
            buildSignals: (trueProbability, _) => (
                Bookmaker: trueProbability,
                Calculator: 1.0 - trueProbability,
                Statistical: (double?)null));
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.RebuildProfilesAsync();

        var profile = await context.EnsembleWeightProfiles
            .SingleAsync(p => p.Market == PredictionMarket.Over25Goals);

        Assert.True(profile.BookmakerWeight > profile.CalculatorWeight);
        Assert.True(profile.CandidateHoldoutBrier < profile.BaselineHoldoutBrier);

        var weights = service.GetWeights(PredictionMarket.Over25Goals, EnsembleWeights.ProductionDefault);
        Assert.Equal(profile.BookmakerWeight, weights.Bookmaker, 9);
        Assert.Equal(profile.CalculatorWeight, weights.Market, 9);
    }

    [Fact]
    public async Task RebuildProfilesAsync_DoesNotPromote_WithInsufficientSamples()
    {
        await using var context = CreateContext();

        SeedForecasts(
            context,
            count: 40,
            buildSignals: (trueProbability, _) => (
                Bookmaker: trueProbability,
                Calculator: 0.5,
                Statistical: (double?)null));
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.RebuildProfilesAsync();

        Assert.Empty(await context.EnsembleWeightProfiles.ToListAsync());

        var weights = service.GetWeights(PredictionMarket.Over25Goals, EnsembleWeights.ProductionDefault);
        Assert.Equal(EnsembleWeights.ProductionDefault, weights);
    }

    [Fact]
    public async Task GetWeights_FallsBackToProvidedDefaults_WhenMarketHasNoProfile()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        var fallback = new EnsembleWeights { Bookmaker = 0.9, Market = 0.8, DixonColes = 0.7 };
        var weights = service.GetWeights(PredictionMarket.BothTeamsScore, fallback);

        Assert.Equal(fallback, weights);
    }

    [Fact]
    public async Task RebuildProfilesAsync_SkipsObservations_WithoutPerSignalContributions()
    {
        await using var context = CreateContext();

        // Legacy rows: no calculatorSignal key. They cannot be replayed, so no profile.
        for (var i = 0; i < 300; i++)
        {
            context.ForecastObservations.Add(BuildObservation(
                index: i,
                featureContributionsJson: "{\"modelOutputs\":{\"over25\":0.6}}",
                outcomeOccurred: i % 2 == 0));
        }
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.RebuildProfilesAsync();

        Assert.Empty(await context.EnsembleWeightProfiles.ToListAsync());
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static EnsembleWeightTuningService CreateService(ApplicationDbContext context)
    {
        return new EnsembleWeightTuningService(context, NullLogger<EnsembleWeightTuningService>.Instance);
    }

    private static void SeedForecasts(
        ApplicationDbContext context,
        int count,
        Func<double, int, (double Bookmaker, double Calculator, double? Statistical)> buildSignals)
    {
        var random = new Random(20260703);
        for (var i = 0; i < count; i++)
        {
            // True probability sweeps the (0.2, 0.8) band deterministically.
            var trueProbability = 0.2 + 0.6 * ((i % 97) / 96.0);
            var outcome = random.NextDouble() < trueProbability;
            var (bookmaker, calculator, statistical) = buildSignals(trueProbability, i);

            var signals = new Dictionary<string, object?>
            {
                ["calculatorSignal"] = new Dictionary<string, double>
                {
                    ["btts"] = calculator,
                    ["over25"] = calculator,
                    ["homeWin"] = calculator,
                    ["awayWin"] = calculator,
                    ["draw"] = calculator
                },
                ["bookmakerSignal"] = new Dictionary<string, double>
                {
                    ["over25"] = bookmaker
                },
                ["statisticalSignal"] = statistical is null
                    ? null
                    : new Dictionary<string, double> { ["over25"] = statistical.Value }
            };

            context.ForecastObservations.Add(BuildObservation(
                index: i,
                featureContributionsJson: JsonSerializer.Serialize(signals),
                outcomeOccurred: outcome));
        }
    }

    private static ForecastObservation BuildObservation(int index, string featureContributionsJson, bool outcomeOccurred)
    {
        var settledAt = DateTime.UtcNow.AddDays(-60).AddHours(index * 2);
        return new ForecastObservation
        {
            Market = PredictionMarket.Over25Goals,
            FixtureKey = $"fixture-{index}",
            MatchLocalDate = DateOnly.FromDateTime(settledAt),
            MatchDateTime = settledAt.AddHours(-3),
            League = "Test League",
            HomeTeam = $"Home {index}",
            AwayTeam = $"Away {index}",
            PredictedOutcome = "Over 2.5",
            RawProbability = 0.6,
            CorrectedProbability = 0.6,
            CalibratedProbability = 0.6,
            FeatureContributionsJson = featureContributionsJson,
            OutcomeOccurred = outcomeOccurred,
            IsSettled = true,
            SettledAt = settledAt,
            CreatedAt = settledAt.AddHours(-4),
            IsCurrentRevision = true,
            RevisionNumber = 1,
            PredictionRunId = Guid.NewGuid()
        };
    }
}
