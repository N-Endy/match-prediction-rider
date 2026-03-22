using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
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
                MatchLocalDate = DateOnly.FromDateTime(today),
                MatchLocalTime = new TimeOnly(18, 0),
                FixtureKey = "today-league|today-fc|current-fc",
                League = "League",
                HomeTeam = "Today FC",
                AwayTeam = "Current FC",
                MatchDateTime = today.AddHours(17)
            },
            new MatchData
            {
                Date = tomorrowString,
                Time = "09:00",
                MatchLocalDate = DateOnly.FromDateTime(tomorrow),
                MatchLocalTime = new TimeOnly(9, 0),
                FixtureKey = "tomorrow-league|tomorrow-fc|future-fc",
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
            new AiScoreSourceHealthTracker(),
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
        Assert.Equal(DateOnly.FromDateTime(tomorrow), savedPrediction.MatchLocalDate);
        Assert.True(savedPrediction.IsCurrentRevision);
        Assert.NotEqual(Guid.Empty, savedPrediction.PredictionRunId);

        var savedForecast = await context.ForecastObservations.SingleAsync();
        Assert.Equal(tomorrowString, savedForecast.Date);
        Assert.Equal("Tomorrow FC", savedForecast.HomeTeam);
        Assert.Equal(DateOnly.FromDateTime(tomorrow), savedForecast.MatchLocalDate);
        Assert.True(savedForecast.IsCurrentRevision);

        var predictionRun = await context.PredictionRuns.SingleAsync();
        Assert.Equal("prediction_generation", predictionRun.RunKind);
        Assert.True(predictionRun.Succeeded);
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
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            MatchLocalTime = new TimeOnly(18, 0),
            FixtureKey = "league|alpha|beta",
            League = "League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            CalibratorUsed = "Unknown",
            ThresholdSource = "Unknown",
            ThresholdUsed = 0,
            WasPublished = false,
            PredictionRunId = Guid.NewGuid(),
            RunLabel = "00:35 WAT",
            RunReason = "legacy"
        });

        context.ForecastObservations.Add(new ForecastObservation
        {
            Date = today,
            Time = "18:00",
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            MatchLocalTime = new TimeOnly(18, 0),
            FixtureKey = "league|alpha|beta",
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
            IsPublished = true,
            PredictionRunId = Guid.NewGuid(),
            RunLabel = "00:35 WAT",
            RunReason = "legacy"
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
            new AiScoreSourceHealthTracker(),
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

    [Fact]
    public async Task GeneratePredictionsAsync_PreservesHistoricalRevisions_AndMarksLatestCurrent()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));
        var targetDateString = targetDate.ToString("dd-MM-yyyy");

        context.MatchDatas.Add(new MatchData
        {
            Date = targetDateString,
            Time = "10:00",
            MatchLocalDate = targetDate,
            MatchLocalTime = new TimeOnly(10, 0),
            MatchDateTime = targetDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc),
            FixtureKey = "league|alpha|beta",
            League = "League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta"
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
            new AiScoreSourceHealthTracker(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.GeneratePredictionsAsync(targetDateString, "initial");
        await service.GeneratePredictionsAsync(targetDateString, "refresh");

        var predictions = await context.Predictions
            .OrderBy(prediction => prediction.RevisionNumber)
            .ToListAsync();
        var forecasts = await context.ForecastObservations
            .OrderBy(forecast => forecast.RevisionNumber)
            .ToListAsync();
        var runs = await context.PredictionRuns
            .OrderBy(run => run.StartedAtUtc)
            .ToListAsync();

        Assert.Equal(2, predictions.Count);
        Assert.Equal(new[] { 1, 2 }, predictions.Select(prediction => prediction.RevisionNumber).ToArray());
        Assert.False(predictions[0].IsCurrentRevision);
        Assert.True(predictions[1].IsCurrentRevision);
        Assert.Equal("initial", predictions[0].RunReason);
        Assert.Equal("refresh", predictions[1].RunReason);
        Assert.NotNull(predictions[0].SupersededAt);

        Assert.Equal(2, forecasts.Count);
        Assert.Equal(new[] { 1, 2 }, forecasts.Select(forecast => forecast.RevisionNumber).ToArray());
        Assert.False(forecasts[0].IsCurrentRevision);
        Assert.True(forecasts[1].IsCurrentRevision);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.True(run.Succeeded));
    }

    [Fact]
    public async Task GeneratePredictionsAsync_PreservesUntouchedCurrentFixtures_WhenRefreshInputIsPartial()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));
        var targetDateString = targetDate.ToString("dd-MM-yyyy");

        context.MatchDatas.AddRange(
            new MatchData
            {
                Date = targetDateString,
                Time = "10:00",
                MatchLocalDate = targetDate,
                MatchLocalTime = new TimeOnly(10, 0),
                MatchDateTime = targetDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc),
                FixtureKey = "league|alpha|beta",
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta"
            },
            new MatchData
            {
                Date = targetDateString,
                Time = "12:00",
                MatchLocalDate = targetDate,
                MatchLocalTime = new TimeOnly(12, 0),
                MatchDateTime = targetDate.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc),
                FixtureKey = "league|gamma|delta",
                League = "League",
                HomeTeam = "Gamma",
                AwayTeam = "Delta"
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
            new AiScoreSourceHealthTracker(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                UnderTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.GeneratePredictionsAsync(targetDateString, "initial");

        var omittedFixture = await context.MatchDatas.SingleAsync(match =>
            match.HomeTeam == "Gamma" &&
            match.AwayTeam == "Delta");
        context.MatchDatas.Remove(omittedFixture);
        await context.SaveChangesAsync();

        await service.GeneratePredictionsAsync(targetDateString, "refresh-partial");

        var predictions = await context.Predictions
            .OrderBy(prediction => prediction.HomeTeam)
            .ThenBy(prediction => prediction.RevisionNumber)
            .ToListAsync();
        var forecasts = await context.ForecastObservations
            .OrderBy(forecast => forecast.HomeTeam)
            .ThenBy(forecast => forecast.RevisionNumber)
            .ToListAsync();

        Assert.Equal(3, predictions.Count);
        Assert.Equal(2, predictions.Count(prediction => prediction.IsCurrentRevision));

        var alphaPredictions = predictions
            .Where(prediction => prediction.HomeTeam == "Alpha" && prediction.AwayTeam == "Beta")
            .OrderBy(prediction => prediction.RevisionNumber)
            .ToList();
        Assert.Equal(2, alphaPredictions.Count);
        Assert.False(alphaPredictions[0].IsCurrentRevision);
        Assert.True(alphaPredictions[1].IsCurrentRevision);
        Assert.Equal("refresh-partial", alphaPredictions[1].RunReason);

        var gammaPrediction = Assert.Single(predictions.Where(prediction =>
            prediction.HomeTeam == "Gamma" &&
            prediction.AwayTeam == "Delta"));
        Assert.True(gammaPrediction.IsCurrentRevision);
        Assert.Equal("initial", gammaPrediction.RunReason);

        Assert.Equal(3, forecasts.Count);
        Assert.Equal(2, forecasts.Count(forecast => forecast.IsCurrentRevision));

        var alphaForecasts = forecasts
            .Where(forecast => forecast.HomeTeam == "Alpha" && forecast.AwayTeam == "Beta")
            .OrderBy(forecast => forecast.RevisionNumber)
            .ToList();
        Assert.Equal(2, alphaForecasts.Count);
        Assert.False(alphaForecasts[0].IsCurrentRevision);
        Assert.True(alphaForecasts[1].IsCurrentRevision);

        var gammaForecast = Assert.Single(forecasts.Where(forecast =>
            forecast.HomeTeam == "Gamma" &&
            forecast.AwayTeam == "Delta"));
        Assert.True(gammaForecast.IsCurrentRevision);
        Assert.Equal("initial", gammaForecast.RunReason);
    }

    [Fact]
    public async Task GeneratePredictionsAsync_RestoresPreKickoffCurrentRevision_WhenRefreshAlreadyReplacedItAfterKickoff()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoffLocal = DateTimeProvider.GetLocalTime().AddMinutes(-30);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal);
        var targetDate = DateOnly.FromDateTime(kickoffLocal);
        var targetDateString = targetDate.ToString("dd-MM-yyyy");
        var fixtureKey = "league|alpha|beta";

        context.MatchDatas.Add(new MatchData
        {
            Date = targetDateString,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = targetDate,
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = fixtureKey,
            League = "League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta"
        });

        context.Predictions.AddRange(
            new Prediction
            {
                Date = targetDateString,
                Time = kickoffLocal.ToString("HH:mm"),
                MatchLocalDate = targetDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
                MatchDateTime = kickoffUtc,
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "StraightWin",
                PredictedOutcome = "Home Win",
                ConfidenceScore = 0.74m,
                RawConfidenceScore = 0.72m,
                CalibratorUsed = "Bucket",
                ThresholdSource = "Configured",
                ThresholdUsed = 0.68,
                WasPublished = true,
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "initial",
                RunReason = "initial",
                RevisionNumber = 1,
                IsCurrentRevision = false,
                CreatedAt = kickoffUtc.AddMinutes(-20)
            },
            new Prediction
            {
                Date = targetDateString,
                Time = kickoffLocal.ToString("HH:mm"),
                MatchLocalDate = targetDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
                MatchDateTime = kickoffUtc,
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "StraightWin",
                PredictedOutcome = "Home Win",
                ConfidenceScore = 0.74m,
                RawConfidenceScore = 0.72m,
                CalibratorUsed = "Bucket",
                ThresholdSource = "Configured",
                ThresholdUsed = 0.68,
                WasPublished = true,
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "refresh",
                RunReason = "refresh",
                RevisionNumber = 2,
                IsCurrentRevision = true,
                ActualScore = "1:0",
                ActualOutcome = "Home Win",
                IsLive = false,
                CreatedAt = kickoffUtc.AddMinutes(30)
            });

        context.ForecastObservations.AddRange(
            new ForecastObservation
            {
                Date = targetDateString,
                Time = kickoffLocal.ToString("HH:mm"),
                MatchLocalDate = targetDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
                MatchDateTime = kickoffUtc,
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                Market = PredictionMarket.HomeWin,
                PredictedOutcome = "Home Win",
                RawProbability = 0.72,
                CalibratedProbability = 0.74,
                CalibratorUsed = "Bucket",
                ThresholdSource = "Configured",
                ThresholdUsed = 0.68,
                IsPublished = true,
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "initial",
                RunReason = "initial",
                RevisionNumber = 1,
                IsCurrentRevision = false,
                CreatedAt = kickoffUtc.AddMinutes(-20)
            },
            new ForecastObservation
            {
                Date = targetDateString,
                Time = kickoffLocal.ToString("HH:mm"),
                MatchLocalDate = targetDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
                MatchDateTime = kickoffUtc,
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                Market = PredictionMarket.HomeWin,
                PredictedOutcome = "Home Win",
                RawProbability = 0.72,
                CalibratedProbability = 0.74,
                CalibratorUsed = "Bucket",
                ThresholdSource = "Configured",
                ThresholdUsed = 0.68,
                IsPublished = true,
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "refresh",
                RunReason = "refresh",
                RevisionNumber = 2,
                IsCurrentRevision = true,
                ActualScore = "1:0",
                ActualOutcome = "Home Win",
                OutcomeOccurred = true,
                IsLive = false,
                IsSettled = true,
                SettledAt = kickoffUtc.AddHours(2),
                CreatedAt = kickoffUtc.AddMinutes(30)
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
            new AiScoreSourceHealthTracker(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                UnderTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.GeneratePredictionsAsync(targetDateString, "refresh-after-kickoff");

        var predictions = await context.Predictions
            .OrderBy(prediction => prediction.RevisionNumber)
            .ToListAsync();
        Assert.Equal(2, predictions.Count);

        Assert.True(predictions[0].IsCurrentRevision);
        Assert.False(predictions[1].IsCurrentRevision);
        Assert.Equal("1:0", predictions[0].ActualScore);
        Assert.Equal("Home Win", predictions[0].ActualOutcome);
        Assert.NotNull(predictions[1].SupersededAt);

        var forecasts = await context.ForecastObservations
            .OrderBy(forecast => forecast.RevisionNumber)
            .ToListAsync();
        Assert.Equal(2, forecasts.Count);

        Assert.True(forecasts[0].IsCurrentRevision);
        Assert.False(forecasts[1].IsCurrentRevision);
        Assert.Equal("1:0", forecasts[0].ActualScore);
        Assert.Equal("Home Win", forecasts[0].ActualOutcome);
        Assert.True(forecasts[0].OutcomeOccurred);
        Assert.True(forecasts[0].IsSettled);
        Assert.NotNull(forecasts[1].SupersededAt);
    }

    [Fact]
    public async Task GeneratePredictionsAsync_DeduplicatesDuplicateFixtureRowsBeforeSaving()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));
        var targetDateString = targetDate.ToString("dd-MM-yyyy");
        var kickoff = targetDate.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);

        context.MatchDatas.AddRange(
            new MatchData
            {
                Date = targetDateString,
                Time = "11:00",
                MatchLocalDate = targetDate,
                MatchLocalTime = new TimeOnly(11, 0),
                MatchDateTime = kickoff,
                FixtureKey = "league|alpha|beta",
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                HomeWin = 0.55,
                Draw = 0.24,
                AwayWin = 0.21
            },
            new MatchData
            {
                Date = targetDateString,
                Time = "11:00",
                MatchLocalDate = targetDate,
                MatchLocalTime = new TimeOnly(11, 0),
                MatchDateTime = kickoff,
                FixtureKey = "league|alpha|beta",
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                HomeWin = 0.54,
                Draw = 0.25,
                AwayWin = 0.21
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
            new AiScoreSourceHealthTracker(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.GeneratePredictionsAsync(targetDateString, "dedupe-test");

        Assert.Single(dataAnalyzer.LastMatchSelection);
        Assert.Single(await context.ForecastObservations.ToListAsync());
        Assert.Single(await context.Predictions.ToListAsync());
    }

    [Fact]
    public async Task RunDailyAnalysisAsync_RebuildsSourceQualityProfiles()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTime.UtcNow.Date.AddHours(18);
        var localDate = DateOnly.FromDateTime(kickoff);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.Add(new Prediction
        {
            Date = date,
            Time = "18:00",
            MatchLocalDate = localDate,
            MatchLocalTime = new TimeOnly(18, 0),
            MatchDateTime = kickoff,
            FixtureKey = "league|alpha|beta",
            League = "League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            ActualScore = "2:1",
            ActualOutcome = "Home Win",
            IsLive = false,
            IsCurrentRevision = true
        });

        context.MatchScores.Add(new MatchScore
        {
            MatchTime = kickoff,
            League = "League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            Score = "2:1",
            BTTSLabel = true,
            IsLive = false
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
            new AiScoreSourceHealthTracker(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.RunDailyAnalysisAsync();

        Assert.NotEmpty(await context.SourceQualityProfiles.ToListAsync());
    }

    [Fact]
    public async Task GeneratePredictionsAsync_CapturesPublishOddsSnapshot_ForCurrentDayPrediction()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var nowLocal = DateTimeProvider.GetLocalTime();
        var kickoffLocal = nowLocal.AddHours(2);
        if (kickoffLocal.Date != nowLocal.Date)
        {
            kickoffLocal = nowLocal.AddMinutes(10);
        }

        var targetDate = DateOnly.FromDateTime(kickoffLocal);
        var targetDateString = targetDate.ToString("dd-MM-yyyy");
        context.MatchDatas.Add(new MatchData
        {
            Date = targetDateString,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = targetDate,
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoffLocal),
            FixtureKey = "league|alpha|beta",
            League = "League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            HomeWin = 0.55,
            Draw = 0.24,
            AwayWin = 0.21
        });

        await context.SaveChangesAsync();

        var sourcePricing = new StubSourceMarketPricingService
        {
            Fixtures =
            [
                new SourceMarketFixture
                {
                    League = "League",
                    HomeTeam = "Alpha",
                    AwayTeam = "Beta",
                    MatchTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal),
                    HomeWinProbability = 0.55,
                    HomeWinOdds = 2.20
                }
            ]
        };

        var service = new AnalyzerService(
            new StubDataAnalyzerService(),
            new StubWebScraperService(),
            context,
            new StubExtractFromExcel(),
            new StubRegressionPredictorService(),
            new StubCalibrationService(),
            new StubThresholdTuningService(),
            sourcePricing,
            new AiScoreSourceHealthTracker(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.GeneratePredictionsAsync(targetDateString, "publish-snapshot-test");

        var snapshot = await context.PredictionOddsSnapshots.SingleAsync();
        Assert.Equal(PredictionOddsSnapshotKind.Publish, snapshot.SnapshotKind);
        Assert.Equal("SportyBet", snapshot.SourceName);
        Assert.Equal(2.20, snapshot.DecimalOdds, 2);
        Assert.Equal(BetPricingMath.ConvertDecimalOddsToProbability(2.20) ?? 0d, snapshot.ImpliedProbability, 5);
        Assert.Equal("Source decimal odds", snapshot.OddsDerivationSource);
    }

    [Fact]
    public async Task CaptureClosingLineSnapshotsAsync_CapturesCloseSnapshotOnce_AndSupportsPositiveClv()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var nowLocal = DateTimeProvider.GetLocalTime();
        var kickoffLocal = nowLocal.AddMinutes(10);
        if (kickoffLocal.Date != nowLocal.Date)
        {
            kickoffLocal = nowLocal.AddMinutes(5);
        }

        var targetDate = DateOnly.FromDateTime(kickoffLocal);
        var targetDateString = targetDate.ToString("dd-MM-yyyy");
        context.MatchDatas.Add(new MatchData
        {
            Date = targetDateString,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = targetDate,
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoffLocal),
            FixtureKey = "league|gamma|delta",
            League = "League",
            HomeTeam = "Gamma",
            AwayTeam = "Delta",
            HomeWin = 0.56,
            Draw = 0.23,
            AwayWin = 0.21
        });

        await context.SaveChangesAsync();

        var sourcePricing = new StubSourceMarketPricingService
        {
            Fixtures =
            [
                new SourceMarketFixture
                {
                    League = "League",
                    HomeTeam = "Gamma",
                    AwayTeam = "Delta",
                    MatchTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal),
                    HomeWinProbability = 0.56,
                    HomeWinOdds = 2.10
                }
            ]
        };

        var service = new AnalyzerService(
            new StubDataAnalyzerService(),
            new StubWebScraperService(),
            context,
            new StubExtractFromExcel(),
            new StubRegressionPredictorService(),
            new StubCalibrationService(),
            new StubThresholdTuningService(),
            sourcePricing,
            new AiScoreSourceHealthTracker(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }),
            NullLogger<AnalyzerService>.Instance);

        await service.GeneratePredictionsAsync(targetDateString, "close-snapshot-test");

        sourcePricing.Fixtures =
        [
            new SourceMarketFixture
            {
                League = "League",
                HomeTeam = "Gamma",
                AwayTeam = "Delta",
                MatchTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal),
                HomeWinProbability = 0.61,
                HomeWinOdds = 1.80
            }
        ];

        await service.CaptureClosingLineSnapshotsAsync(15);
        await service.CaptureClosingLineSnapshotsAsync(15);

        var snapshots = await context.PredictionOddsSnapshots
            .OrderBy(snapshot => snapshot.SnapshotKind)
            .ToListAsync();

        Assert.Equal(2, snapshots.Count);
        var publish = Assert.Single(snapshots.Where(snapshot => snapshot.SnapshotKind == PredictionOddsSnapshotKind.Publish));
        var close = Assert.Single(snapshots.Where(snapshot => snapshot.SnapshotKind == PredictionOddsSnapshotKind.Close));
        Assert.Equal(2.10, publish.DecimalOdds, 2);
        Assert.Equal(1.80, close.DecimalOdds, 2);
        Assert.True(BetPricingMath.CalculateClosingLineValuePercent(publish.DecimalOdds, close.DecimalOdds) > 0);
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
                MatchLocalDate = match.MatchLocalDate ?? DateOnly.ParseExact(match.Date ?? string.Empty, "dd-MM-yyyy"),
                MatchLocalTime = match.MatchLocalTime,
                MatchDateTime = match.MatchDateTime,
                FixtureKey = match.FixtureKey,
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
        public IReadOnlyList<PredictionCandidate> UnderTwoGoals(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches) => [];
    }

    private sealed class StubWebScraperService : IWebScraperService
    {
        public Task ScrapeMatchDataAsync() => Task.CompletedTask;
        public Task<List<MatchScore>> ScrapeMatchScoresAsync() => Task.FromResult(new List<MatchScore>());
        public Task<List<MatchScore>> ScrapeTennisScoresMatchScoresAsync() => Task.FromResult(new List<MatchScore>());
        public Task<List<AiScoreMatchScore>> ScrapeAiScoreMatchScoresAsync() => Task.FromResult(new List<AiScoreMatchScore>());
        public Task<List<SofaScoreMatchScore>> ScrapeSofaScoreMatchScoresAsync(IEnumerable<SofaScoreFixtureRequest> fixtures) => Task.FromResult(new List<SofaScoreMatchScore>());
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
        public IReadOnlyList<SourceMarketFixture> Fixtures { get; set; } = [];

        public Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default) =>
            Task.FromResult(Fixtures);
    }
}
