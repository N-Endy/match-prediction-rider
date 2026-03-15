using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AnalyzerServiceBackfillTests
{
    [Fact]
    public async Task GeneratePredictionsAsync_TargetDate_UsesMatchingDayFixturesOnly()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);
        var tomorrowString = tomorrow.ToString("dd-MM-yyyy");

        context.MatchDatas.AddRange(
            new MatchData
            {
                Date = today.ToString("dd-MM-yyyy"),
                Time = "18:00",
                League = "League",
                HomeTeam = "Today FC",
                AwayTeam = "Current FC",
                MatchDateTime = today.AddHours(17)
            },
            new MatchData
            {
                Date = tomorrowString,
                Time = "09:00",
                League = "League",
                HomeTeam = "Tomorrow FC",
                AwayTeam = "Future FC",
                MatchDateTime = tomorrow.AddHours(8)
            });

        await context.SaveChangesAsync();

        var dataAnalyzer = new StubDataAnalyzerService();
        var service = new AnalyzerService(
            dataAnalyzer,
            new StubWebScraperService(),
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
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.GeneratePredictionsAsync(tomorrowString);

        var selectedHomeTeams = dataAnalyzer.LastMatchSelection.Select(match => match.HomeTeam).ToList();
        var selectedHomeTeam = Assert.Single(selectedHomeTeams);
        Assert.Equal("Tomorrow FC", selectedHomeTeam);

        var savedPrediction = await context.Predictions.SingleAsync();
        Assert.Equal(tomorrowString, savedPrediction.Date);
        Assert.Equal("Tomorrow FC", savedPrediction.HomeTeam);

        var savedForecast = await context.ForecastObservations.SingleAsync();
        Assert.Equal(tomorrowString, savedForecast.Date);
        Assert.Equal("Tomorrow FC", savedForecast.HomeTeam);
    }

    [Fact]
    public async Task BackfillDecisionProvenanceAsync_FillsUnknownPredictionAndForecastMetadata()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var today = DateTime.UtcNow.ToString("dd-MM-yyyy");

        context.Predictions.Add(new Prediction
        {
            Date = today,
            Time = "18:00",
            League = "League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            CalibratorUsed = "Unknown",
            ThresholdSource = "Unknown",
            ThresholdUsed = 0,
            WasPublished = false
        });

        context.ForecastObservations.Add(new ForecastObservation
        {
            Date = today,
            Time = "18:00",
            League = "League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            Market = PredictionMarket.Over25Goals,
            PredictedOutcome = "Over 2.5",
            RawProbability = 0.61,
            CalibratedProbability = 0.64,
            CalibratorUsed = "Unknown",
            ThresholdSource = "Unknown",
            ThresholdUsed = 0,
            IsPublished = true
        });

        await context.SaveChangesAsync();

        var service = new AnalyzerService(
            new StubDataAnalyzerService(),
            new StubWebScraperService(),
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
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.BackfillDecisionProvenanceAsync(1);

        var prediction = await context.Predictions.SingleAsync();
        var forecast = await context.ForecastObservations.SingleAsync();

        Assert.Equal("Bucket", prediction.CalibratorUsed);
        Assert.Equal("Configured", prediction.ThresholdSource);
        Assert.Equal(0.68, prediction.ThresholdUsed, 3);
        Assert.True(prediction.WasPublished);

        Assert.Equal("Bucket", forecast.CalibratorUsed);
        Assert.Equal("Configured", forecast.ThresholdSource);
        Assert.Equal(0.58, forecast.ThresholdUsed, 3);
    }

    private sealed class StubDataAnalyzerService : IDataAnalyzerService
    {
        public List<MatchData> LastMatchSelection { get; } = [];

        public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches)
        {
            var selectedMatches = matches.ToList();
            LastMatchSelection.Clear();
            LastMatchSelection.AddRange(selectedMatches);

            return selectedMatches.Select(match => new PredictionCandidate
            {
                Market = PredictionMarket.HomeWin,
                Date = match.Date ?? string.Empty,
                Time = match.Time ?? string.Empty,
                MatchDateTime = match.MatchDateTime,
                League = match.League ?? string.Empty,
                HomeTeam = match.HomeTeam ?? string.Empty,
                AwayTeam = match.AwayTeam ?? string.Empty,
                PredictionCategory = "StraightWin",
                PredictedOutcome = "Home Win",
                RawProbability = 0.72,
                CalibratedProbability = 0.74,
                CalibratorUsed = "Bucket"
            }).ToList();
        }

        public IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates) =>
            forecastCandidates.Select(candidate =>
            {
                candidate.WasPublished = true;
                candidate.ThresholdUsed = 0.68;
                candidate.ThresholdSource = "Configured";
                return candidate;
            }).ToList();

        public IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> Draw(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches) => [];
    }

    private sealed class StubWebScraperService : IWebScraperService
    {
        public Task ScrapeMatchDataAsync() => Task.CompletedTask;
        public Task<List<MatchScore>> ScrapeMatchScoresAsync() => Task.FromResult(new List<MatchScore>());
        public Task<List<AiScoreMatchScore>> ScrapeAiScoreMatchScoresAsync() => Task.FromResult(new List<AiScoreMatchScore>());
    }

    private sealed class StubExtractFromExcel : IExtractFromExcel
    {
        public IEnumerable<MatchData> ExtractMatchDatasetFromFile(DateTime? targetLocalDate = null) => [];
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
