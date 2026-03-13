using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ScoreUpdaterMatchingTests
{
    [Fact]
    public async Task RunScoreUpdaterAsync_SkipsAmbiguousShortenedClubNames()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().Date.AddHours(20);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.AddRange(
            CreatePrediction(date, kickoff, "Real Madrid", "Athletic Bilbao", "StraightWin", "Home Win"),
            CreatePrediction(date, kickoff, "Atletico Madrid", "Athletic Bilbao", "StraightWin", "Home Win"));

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = kickoff,
                        League = "Spain LaLiga",
                        HomeTeam = "Madrid",
                        AwayTeam = "Athletic Bilbao",
                        Score = "2:1",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var predictions = await context.Predictions.OrderBy(prediction => prediction.HomeTeam).ToListAsync();
        Assert.All(predictions, prediction => Assert.Null(prediction.ActualScore));
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_SettlesReserveFixtureWhenQualifierAndAliasesAgree()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().Date.AddHours(15).AddMinutes(30);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.Add(CreatePrediction(
            date,
            kickoff,
            "Slavia Prague B",
            "Zaglebie II",
            "Draw",
            "Draw",
            "WORLD: Club Friendly"));

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = kickoff,
                        League = "World Club Friendly",
                        HomeTeam = "Slavia Prague B (Cze)",
                        AwayTeam = "Zaglebie II (Pol)",
                        Score = "2:2",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Equal("2:2", prediction.ActualScore);
        Assert.Equal("Draw", prediction.ActualOutcome);
        Assert.False(prediction.IsLive);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_SettlesWomenFixtureWhenLeagueProvidesQualifierContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().Date.AddHours(9);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.Add(CreatePrediction(
            date,
            kickoff,
            "Newcastle Jets",
            "Sydney FC",
            "BothTeamsScore",
            "BTTS",
            "Australia - A League Women"));

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                AiScoreMatchScores =
                [
                    new AiScoreMatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
                        League = "Australia W-League",
                        HomeTeam = "Newcastle Jets Women",
                        AwayTeam = "Sydney FC Women",
                        Score = "3:1",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Equal("3:1", prediction.ActualScore);
        Assert.Equal("BTTS", prediction.ActualOutcome);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_BackfillsUnresolvedPredictionFromPreviousDay()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().Date.AddDays(-1).AddHours(18);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.Add(CreatePrediction(
            date,
            kickoff,
            "Sao Paulo",
            "Santos",
            "StraightWin",
            "Home Win",
            "Brazil - Serie A"));

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
                        League = "Brazil Serie A",
                        HomeTeam = "São Paulo FC",
                        AwayTeam = "Santos",
                        Score = "2:0",
                        BTTSLabel = false,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Equal("2:0", prediction.ActualScore);
        Assert.Equal("Home Win", prediction.ActualOutcome);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_RepairsMissingActualOutcomesFromStoredScores()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().Date.AddHours(10);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.AddRange(
            CreateSettledPrediction(date, kickoff, "Belmont Swansea United", "Weston Workers", "BothTeamsScore", "BTTS", "2:4"),
            CreateSettledPrediction(date, kickoff, "Broadmeadow Magic", "Adamstown Rosebud", "Over2.5Goals", "Over 2.5", "2:2"),
            CreateSettledPrediction(date, kickoff, "West Adelaide", "MetroStars", "StraightWin", "Home Win", "5:1"),
            CreateSettledPrediction(date, kickoff, "Brisbane Roar", "Western Sydney Wanderers", "Draw", "Draw", "2:2"));

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(context, new StubWebScraperService());

        await service.RunScoreUpdaterAsync();

        var predictions = await context.Predictions
            .OrderBy(prediction => prediction.HomeTeam)
            .ToListAsync();

        Assert.Collection(
            predictions,
            prediction => Assert.Equal("BTTS", prediction.ActualOutcome),
            prediction => Assert.Equal("Draw", prediction.ActualOutcome),
            prediction => Assert.Equal("Over 2.5", prediction.ActualOutcome),
            prediction => Assert.Equal("Home Win", prediction.ActualOutcome));
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_SettlesStaleLivePredictionFromStoredScore()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().AddHours(-5);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.Add(new Prediction
        {
            Date = date,
            Time = kickoff.ToString("HH:mm"),
            MatchDateTime = null,
            League = "Australia - Test League",
            HomeTeam = "Belmont Swansea United",
            AwayTeam = "Weston Workers",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ActualScore = "2:4",
            ActualOutcome = null,
            IsLive = true
        });

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(context, new StubWebScraperService());

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.False(prediction.IsLive);
        Assert.Equal("BTTS", prediction.ActualOutcome);
    }

    private static Prediction CreatePrediction(
        string date,
        DateTime kickoff,
        string homeTeam,
        string awayTeam,
        string predictionCategory,
        string predictedOutcome,
        string league = "Spain LaLiga")
    {
        return new Prediction
        {
            Date = date,
            Time = kickoff.ToString("HH:mm"),
            MatchDateTime = kickoff,
            League = league,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            PredictionCategory = predictionCategory,
            PredictedOutcome = predictedOutcome,
            IsLive = false
        };
    }

    private static Prediction CreateSettledPrediction(
        string date,
        DateTime kickoff,
        string homeTeam,
        string awayTeam,
        string predictionCategory,
        string predictedOutcome,
        string actualScore,
        string league = "Australia - Test League")
    {
        return new Prediction
        {
            Date = date,
            Time = kickoff.ToString("HH:mm"),
            MatchDateTime = kickoff,
            League = league,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            PredictionCategory = predictionCategory,
            PredictedOutcome = predictedOutcome,
            ActualScore = actualScore,
            ActualOutcome = null,
            IsLive = false
        };
    }

    private static AnalyzerService CreateAnalyzerService(ApplicationDbContext context, StubWebScraperService scraper)
    {
        return new AnalyzerService(
            new StubDataAnalyzerService(),
            scraper,
            context,
            new StubExtractFromExcel(),
            new StubRegressionPredictorService(),
            new StubCalibrationService(),
            new StubThresholdTuningService(),
            new StubSourceMarketPricingService(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.54,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);
    }

    private sealed class StubDataAnalyzerService : IDataAnalyzerService
    {
        public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates) => [];
        public IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> Draw(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches) => [];
    }

    private sealed class StubWebScraperService : IWebScraperService
    {
        public List<MatchScore> MatchScores { get; init; } = [];
        public List<AiScoreMatchScore> AiScoreMatchScores { get; init; } = [];

        public Task ScrapeMatchDataAsync() => Task.CompletedTask;
        public Task<List<MatchScore>> ScrapeMatchScoresAsync() => Task.FromResult(MatchScores);
        public Task<List<AiScoreMatchScore>> ScrapeAiScoreMatchScoresAsync() => Task.FromResult(AiScoreMatchScores);
    }

    private sealed class StubExtractFromExcel : IExtractFromExcel
    {
        public IEnumerable<MatchData> ExtractMatchDatasetFromFile() => [];
    }

    private sealed class StubRegressionPredictorService : IRegressionPredictorService
    {
        public IEnumerable<RegressionPrediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches) => [];
    }

    private sealed class StubCalibrationService : ICalibrationService
    {
        public double Calibrate(PredictionMarket market, double rawProbability) => rawProbability;

        public CalibrationDecision CalibrateWithDecision(PredictionMarket market, double rawProbability) =>
            new()
            {
                Probability = rawProbability,
                CalibratorUsed = "Bucket"
            };

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }

    private sealed class StubThresholdTuningService : IThresholdTuningService
    {
        public double GetThreshold(PredictionMarket market, double fallbackThreshold) => fallbackThreshold;

        public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold) =>
            new()
            {
                Threshold = fallbackThreshold,
                ThresholdSource = "Configured"
            };

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }

    private sealed class StubSourceMarketPricingService : ISourceMarketPricingService
    {
        public Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SourceMarketFixture>>([]);
    }
}
