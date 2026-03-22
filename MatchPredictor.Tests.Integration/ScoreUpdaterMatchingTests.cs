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
        var kickoff = GetStartedKickoffForTodayOrYesterday(15, 30);
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
        var kickoff = GetStartedKickoffForTodayOrYesterday(9);
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
    public async Task RunScoreUpdaterAsync_SettlesAllMarketsForSharedFixtureFromSingleScoreMatch()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetStartedKickoffForTodayOrYesterday(19);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.AddRange(
            CreatePrediction(date, kickoff, "Adelaide United", "Perth Glory", "BothTeamsScore", "BTTS", "Australia - A League Women"),
            CreatePrediction(date, kickoff, "Adelaide United", "Perth Glory", "Over2.5Goals", "Over 2.5", "Australia - A League Women"),
            CreatePrediction(date, kickoff, "Adelaide United", "Perth Glory", "StraightWin", "Home Win", "Australia - A League Women"));

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
                        HomeTeam = "Adelaide United Women",
                        AwayTeam = "Perth Glory Women",
                        Score = "2:1",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var predictions = await context.Predictions
            .OrderBy(prediction => prediction.PredictionCategory)
            .ToListAsync();

        Assert.Collection(
            predictions,
            prediction =>
            {
                Assert.Equal("2:1", prediction.ActualScore);
                Assert.Equal("BTTS", prediction.ActualOutcome);
            },
            prediction =>
            {
                Assert.Equal("2:1", prediction.ActualScore);
                Assert.Equal("Over 2.5", prediction.ActualOutcome);
            },
            prediction =>
            {
                Assert.Equal("2:1", prediction.ActualScore);
                Assert.Equal("Home Win", prediction.ActualOutcome);
            });
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_UsesTargetedSofaScoreFallbackForIncompleteFixture()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetStartedKickoffForTodayOrYesterday(18);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.Add(CreatePrediction(
            date,
            kickoff,
            "Manchester City",
            "Real Madrid",
            "StraightWin",
            "Home Win",
            "UEFA Champions League"));

        await context.SaveChangesAsync();

        var aiScoreTracker = new AiScoreSourceHealthTracker();
        aiScoreTracker.RecordFallback("api-football", 0, "Stubbed AiScore fallback produced no rows.");

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                SofaScoreMatchScores =
                [
                    new SofaScoreMatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
                        League = "UEFA Champions League",
                        HomeTeam = "Manchester City",
                        AwayTeam = "Real Madrid",
                        Score = "2:1",
                        DisplayedScore = "2:1",
                        RegularTimeScore = "2:1",
                        StatusText = "Finished",
                        EventUrl = "https://www.sofascore.com/football/match/real-madrid-manchester-city/rsEgb",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            },
            aiScoreTracker);

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Equal("2:1", prediction.ActualScore);
        Assert.Equal("Home Win", prediction.ActualOutcome);
        Assert.False(prediction.IsLive);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_PersistsSharedSourceRuntimeSnapshotsToScrapingLogs()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var aiScoreTracker = new AiScoreSourceHealthTracker();
        aiScoreTracker.RecordSuccess("http", 14, "Fetched 14 match(es) from AiScore.");

        var sofaScoreTracker = new SofaScoreSourceHealthTracker();
        sofaScoreTracker.RecordAttempt("browser-discovery", "Targeted fallback discovery.");
        sofaScoreTracker.RecordSuccess("event-page", 2, 3, 2, "SofaScore returned 2 match(es).");

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService(),
            aiScoreTracker,
            sofaScoreTracker);

        await service.RunScoreUpdaterAsync();

        var runtimeLogs = await context.ScrapingLogs
            .Where(log => log.EventName == "source_runtime_aiscore" || log.EventName == "source_runtime_sofascore")
            .OrderBy(log => log.EventName)
            .ToListAsync();

        Assert.Collection(
            runtimeLogs,
            log =>
            {
                Assert.Equal("source_runtime_aiscore", log.EventName);
                Assert.Equal("Healthy", log.Status);
                Assert.Contains("\"LastMatchCount\":14", log.Message);
            },
            log =>
            {
                Assert.Equal("source_runtime_sofascore", log.EventName);
                Assert.Equal("Healthy", log.Status);
                Assert.Contains("\"LastMatchCount\":2", log.Message);
            });
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_EarlySettlesBttsAndOverMarketsWhileKeepingStraightWinLive()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetStartedKickoffForTodayOrYesterday(16);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.AddRange(
            CreatePrediction(date, kickoff, "Alpha FC", "Beta FC", "BothTeamsScore", "BTTS", "League"),
            CreatePrediction(date, kickoff, "Alpha FC", "Beta FC", "Over2.5Goals", "Over 2.5", "League"),
            CreatePrediction(date, kickoff, "Alpha FC", "Beta FC", "StraightWin", "Home Win", "League"));

        context.ForecastObservations.AddRange(
            CreateForecast(date, kickoff, "Alpha FC", "Beta FC", PredictionMarket.BothTeamsScore, "BTTS", "League"),
            CreateForecast(date, kickoff, "Alpha FC", "Beta FC", PredictionMarket.Over25Goals, "Over 2.5", "League"),
            CreateForecast(date, kickoff, "Alpha FC", "Beta FC", PredictionMarket.StraightWin, "Home Win", "League"));

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff.AddMinutes(72)),
                        League = "League",
                        HomeTeam = "Alpha FC",
                        AwayTeam = "Beta FC",
                        Score = "2:1",
                        BTTSLabel = true,
                        IsLive = true
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var predictions = await context.Predictions
            .OrderBy(prediction => prediction.PredictionCategory)
            .ToListAsync();
        var forecasts = await context.ForecastObservations
            .OrderBy(forecast => forecast.Market)
            .ToListAsync();

        Assert.Collection(
            predictions,
            prediction =>
            {
                Assert.Equal("2:1", prediction.ActualScore);
                Assert.Equal("BTTS", prediction.ActualOutcome);
                Assert.False(prediction.IsLive);
            },
            prediction =>
            {
                Assert.Equal("2:1", prediction.ActualScore);
                Assert.Equal("Over 2.5", prediction.ActualOutcome);
                Assert.False(prediction.IsLive);
            },
            prediction =>
            {
                Assert.Equal("2:1", prediction.ActualScore);
                Assert.Null(prediction.ActualOutcome);
                Assert.True(prediction.IsLive);
            });

        Assert.Collection(
            forecasts,
            forecast =>
            {
                Assert.Equal("2:1", forecast.ActualScore);
                Assert.Equal("BTTS", forecast.ActualOutcome);
                Assert.True(forecast.OutcomeOccurred);
                Assert.True(forecast.IsSettled);
                Assert.False(forecast.IsLive);
            },
            forecast =>
            {
                Assert.Equal("2:1", forecast.ActualScore);
                Assert.Equal("Over 2.5", forecast.ActualOutcome);
                Assert.True(forecast.OutcomeOccurred);
                Assert.True(forecast.IsSettled);
                Assert.False(forecast.IsLive);
            },
            forecast =>
            {
                Assert.Equal("2:1", forecast.ActualScore);
                Assert.Null(forecast.ActualOutcome);
                Assert.Null(forecast.OutcomeOccurred);
                Assert.False(forecast.IsSettled);
                Assert.True(forecast.IsLive);
            });
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_LeavesBttsAndOverLiveUntilIrreversibleConditionIsMet()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetStartedKickoffForTodayOrYesterday(17);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.AddRange(
            CreatePrediction(date, kickoff, "Gamma FC", "Delta FC", "BothTeamsScore", "BTTS", "League"),
            CreatePrediction(date, kickoff, "Gamma FC", "Delta FC", "Over2.5Goals", "Over 2.5", "League"));

        context.ForecastObservations.AddRange(
            CreateForecast(date, kickoff, "Gamma FC", "Delta FC", PredictionMarket.BothTeamsScore, "BTTS", "League"),
            CreateForecast(date, kickoff, "Gamma FC", "Delta FC", PredictionMarket.Over25Goals, "Over 2.5", "League"));

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff.AddMinutes(50)),
                        League = "League",
                        HomeTeam = "Gamma FC",
                        AwayTeam = "Delta FC",
                        Score = "1:0",
                        BTTSLabel = false,
                        IsLive = true
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var predictions = await context.Predictions
            .OrderBy(prediction => prediction.PredictionCategory)
            .ToListAsync();
        var forecasts = await context.ForecastObservations
            .OrderBy(forecast => forecast.Market)
            .ToListAsync();

        Assert.All(predictions, prediction =>
        {
            Assert.Equal("1:0", prediction.ActualScore);
            Assert.Null(prediction.ActualOutcome);
            Assert.True(prediction.IsLive);
        });

        Assert.All(forecasts, forecast =>
        {
            Assert.Equal("1:0", forecast.ActualScore);
            Assert.Null(forecast.ActualOutcome);
            Assert.Null(forecast.OutcomeOccurred);
            Assert.False(forecast.IsSettled);
            Assert.True(forecast.IsLive);
        });
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_PrefersFinishedFlashScoreSnapshotOverEarlierLiveSnapshots()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoffLocal = DateTimeProvider.GetLocalTime().Date.AddHours(8);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal);
        var date = kickoffLocal.ToString("dd-MM-yyyy");

        context.Predictions.Add(CreatePrediction(
            date,
            kickoffUtc,
            "North District",
            "Hong Kong Rangers",
            "BothTeamsScore",
            "BTTS",
            "Hong Kong - Premier League"));

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = kickoffUtc.AddMinutes(5),
                        League = "HONG KONG: Premier League",
                        HomeTeam = "North District",
                        AwayTeam = "Hong Kong Rangers",
                        Score = "0:0",
                        BTTSLabel = false,
                        IsLive = true
                    },
                    new MatchScore
                    {
                        MatchTime = kickoffUtc.AddMinutes(40),
                        League = "HONG KONG: Premier League",
                        HomeTeam = "North District",
                        AwayTeam = "Hong Kong Rangers",
                        Score = "2:1",
                        BTTSLabel = true,
                        IsLive = true
                    },
                    new MatchScore
                    {
                        MatchTime = kickoffUtc,
                        League = "HONG KONG: Premier League",
                        HomeTeam = "North District",
                        AwayTeam = "Hong Kong Rangers",
                        Score = "2:1",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Equal("2:1", prediction.ActualScore);
        Assert.Equal("BTTS", prediction.ActualOutcome);
        Assert.False(prediction.IsLive);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_DoesNotSettleFutureFixture_AndClearsAnyStaleFutureScore()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var nowLocal = DateTimeProvider.GetLocalTime();
        var kickoffLocal = GetFutureKickoffForToday(nowLocal);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal);
        var sourceKickoffUtc = kickoffUtc.AddHours(-2);
        var date = kickoffLocal.ToString("dd-MM-yyyy");

        context.Predictions.Add(new Prediction
        {
            Date = date,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoffLocal),
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = "switzerland-super-league|st-gallen|fc-lugano",
            League = "Switzerland - Super League",
            HomeTeam = "St. Gallen",
            AwayTeam = "FC Lugano",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ActualScore = "2:1",
            ActualOutcome = "BTTS",
            IsLive = true
        });

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = sourceKickoffUtc,
                        League = "SWITZERLAND: Super League",
                        HomeTeam = "St. Gallen",
                        AwayTeam = "Lugano",
                        Score = "2:1",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Null(prediction.ActualScore);
        Assert.Null(prediction.ActualOutcome);
        Assert.False(prediction.IsLive);
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
    public async Task RunScoreUpdaterAsync_DefaultRecentWindowSkipsOlderFixture_UntilBackfillRuns()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().Date.AddDays(-3).AddHours(18);
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
        Assert.Null(prediction.ActualScore);

        await service.RunScoreUpdaterAsync(14, "backfill");

        prediction = await context.Predictions.SingleAsync();
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
        var kickoff = GetStartedKickoffForTodayOrYesterday(10);
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
    public async Task RunScoreUpdaterAsync_RefreshesSettledScoreWhenFinalSourceScoreChanges()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().Date.AddHours(7);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.Add(new Prediction
        {
            Date = date,
            Time = kickoff.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoff),
            MatchLocalTime = TimeOnly.FromDateTime(kickoff),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            FixtureKey = "japan-j-league|kashima-antlers|kawasaki-frontale",
            League = "Japan - J League",
            HomeTeam = "Kashima Antlers",
            AwayTeam = "Kawasaki Frontale",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "No BTTS",
            ActualScore = "0:0",
            ActualOutcome = "No BTTS",
            IsLive = false
        });

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
                        League = "JAPAN: J1 League",
                        HomeTeam = "Kashima Antlers",
                        AwayTeam = "Kawasaki Frontale",
                        Score = "1:0",
                        BTTSLabel = false,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Equal("1:0", prediction.ActualScore);
        Assert.Equal("No BTTS", prediction.ActualOutcome);
        Assert.False(prediction.IsLive);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_ReconcilesExactFinishedSourceForStaleLivePredictions()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetStartedKickoffForTodayOrYesterday(9);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoff);
        var date = kickoff.ToString("dd-MM-yyyy");

        context.Predictions.AddRange(
            new Prediction
            {
                Date = date,
                Time = kickoff.ToString("HH:mm"),
                MatchLocalDate = DateOnly.FromDateTime(kickoff),
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoffUtc,
                FixtureKey = "australia-nsw-league-one|bulls-academy|prospect-united",
                League = "Australia - New South Wales League 1",
                HomeTeam = "Bulls Academy",
                AwayTeam = "Prospect United",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "No BTTS",
                ActualScore = "5:0",
                ActualOutcome = null,
                IsLive = true
            },
            new Prediction
            {
                Date = date,
                Time = kickoff.ToString("HH:mm"),
                MatchLocalDate = DateOnly.FromDateTime(kickoff),
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoffUtc,
                FixtureKey = "australia-nsw-league-one|bulls-academy|prospect-united",
                League = "Australia - New South Wales League 1",
                HomeTeam = "Bulls Academy",
                AwayTeam = "Prospect United",
                PredictionCategory = "Over2.5Goals",
                PredictedOutcome = "Over 2.5",
                ActualScore = "5:0",
                ActualOutcome = null,
                IsLive = true
            });

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = kickoffUtc,
                        League = "AUSTRALIA: NSW League One",
                        HomeTeam = "Bulls Academy",
                        AwayTeam = "Prospect United",
                        Score = "5:0",
                        BTTSLabel = false,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var predictions = await context.Predictions
            .OrderBy(prediction => prediction.PredictionCategory)
            .ToListAsync();

        Assert.Collection(
            predictions,
            prediction =>
            {
                Assert.Equal("5:0", prediction.ActualScore);
                Assert.Equal("No BTTS", prediction.ActualOutcome);
                Assert.False(prediction.IsLive);
            },
            prediction =>
            {
                Assert.Equal("5:0", prediction.ActualScore);
                Assert.Equal("Over 2.5", prediction.ActualOutcome);
                Assert.False(prediction.IsLive);
            });
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_ReconcilesExactFinishedSource_WhenKickoffTimeDriftsBeyondDefaultWindow()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoffLocal = DateTimeProvider.GetLocalTime().Date.AddHours(6).AddMinutes(15);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal);
        var date = kickoffLocal.ToString("dd-MM-yyyy");

        context.Predictions.Add(new Prediction
        {
            Date = date,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoffLocal),
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = "australia-npl-victoria|hume-city|dandenong-thunder",
            League = "Australia - NPL Victoria",
            HomeTeam = "Hume City",
            AwayTeam = "Dandenong Thunder",
            PredictionCategory = "Over2.5Goals",
            PredictedOutcome = "Over 2.5",
            ActualScore = "1:1",
            ActualOutcome = "Under 2.5",
            IsLive = false
        });

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoffLocal.AddMinutes(-75)),
                        League = "AUSTRALIA: NPL Victoria",
                        HomeTeam = "Hume City",
                        AwayTeam = "Dandenong Thunder",
                        Score = "3:1",
                        BTTSLabel = true,
                        IsLive = false
                    },
                    new MatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoffLocal.AddMinutes(-80)),
                        League = "AUSTRALIA: NPL Victoria",
                        HomeTeam = "Hume City",
                        AwayTeam = "Dandenong Thunder",
                        Score = "3:1",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Equal("3:1", prediction.ActualScore);
        Assert.Equal("Over 2.5", prediction.ActualOutcome);
        Assert.False(prediction.IsLive);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_ReopensSettledPrediction_WhenOnlyLiveSourceSnapshotExists()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoffLocal = DateTimeProvider.GetLocalTime().Date.AddHours(6).AddMinutes(45);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal);
        var date = kickoffLocal.ToString("dd-MM-yyyy");

        context.Predictions.Add(new Prediction
        {
            Date = date,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoffLocal),
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = "australia-tasmania-npl|riverside-olympic|glenorchy-knights",
            League = "Australia - Tasmania NPL",
            HomeTeam = "Riverside Olympic",
            AwayTeam = "Glenorchy Knights",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            ActualScore = "2:0",
            ActualOutcome = "Home Win",
            IsLive = false
        });

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoffLocal.AddMinutes(95)),
                        League = "AUSTRALIA: Tasmania NPL",
                        HomeTeam = "Riverside Olympic",
                        AwayTeam = "Glenorchy Knights",
                        Score = "2:0",
                        BTTSLabel = false,
                        IsLive = true
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.True(prediction.IsLive);
        Assert.Equal("2:0", prediction.ActualScore);
        Assert.Null(prediction.ActualOutcome);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_PrefersLatestLiveSnapshot_WhenExactPairHasMultipleLiveCandidates()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoffLocal = DateTimeProvider.GetLocalTime().Date.AddHours(6).AddMinutes(45);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal);
        var date = kickoffLocal.ToString("dd-MM-yyyy");

        context.Predictions.Add(new Prediction
        {
            Date = date,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoffLocal),
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = "australia-tasmania-npl|riverside-olympic|glenorchy-knights",
            League = "Australia - Tasmania NPL",
            HomeTeam = "Riverside Olympic",
            AwayTeam = "Glenorchy Knights",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            IsLive = true
        });

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoffLocal.AddMinutes(5)),
                        League = "AUSTRALIA: Tasmania NPL",
                        HomeTeam = "Riverside Olympic",
                        AwayTeam = "Glenorchy Knights",
                        Score = "0:0",
                        BTTSLabel = false,
                        IsLive = true
                    },
                    new MatchScore
                    {
                        MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoffLocal.AddMinutes(95)),
                        League = "Australia - Tasmania NPL",
                        HomeTeam = "Riverside Olympic",
                        AwayTeam = "Glenorchy Knights",
                        Score = "2:0",
                        BTTSLabel = false,
                        IsLive = true
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.True(prediction.IsLive);
        Assert.Equal("2:0", prediction.ActualScore);
        Assert.Null(prediction.ActualOutcome);
    }

    [Fact]
    public async Task RunScoreUpdaterAsync_PrefersHigherQualitySourceWhenExactFinishedScoresConflict()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoffLocal = GetStartedKickoffForTodayOrYesterday(14);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal);
        var date = kickoffLocal.ToString("dd-MM-yyyy");

        context.Predictions.Add(CreatePrediction(
            date,
            kickoffUtc,
            "Alpha FC",
            "Beta FC",
            "StraightWin",
            "Home Win",
            "League"));

        context.SourceQualityProfiles.AddRange(
            new SourceQualityProfile
            {
                SourceName = "FlashScore",
                LeagueKey = "all",
                LeagueLabel = "All Leagues",
                TimeBucketKey = "all",
                TimeBucketLabel = "All Kickoffs",
                SampleCount = 12,
                FinishedCoverageCount = 10,
                ExactScoreMatchCount = 6,
                LiveOnlyCount = 1,
                ReliabilityScore = 0.46
            },
            new SourceQualityProfile
            {
                SourceName = "AiScore",
                LeagueKey = "all",
                LeagueLabel = "All Leagues",
                TimeBucketKey = "all",
                TimeBucketLabel = "All Kickoffs",
                SampleCount = 12,
                FinishedCoverageCount = 11,
                ExactScoreMatchCount = 10,
                LiveOnlyCount = 0,
                ReliabilityScore = 0.88
            });

        await context.SaveChangesAsync();

        var service = CreateAnalyzerService(
            context,
            new StubWebScraperService
            {
                MatchScores =
                [
                    new MatchScore
                    {
                        MatchTime = kickoffUtc,
                        League = "League",
                        HomeTeam = "Alpha FC",
                        AwayTeam = "Beta FC",
                        Score = "1:0",
                        BTTSLabel = false,
                        IsLive = false
                    }
                ],
                AiScoreMatchScores =
                [
                    new AiScoreMatchScore
                    {
                        MatchTime = kickoffUtc,
                        League = "League",
                        HomeTeam = "Alpha FC",
                        AwayTeam = "Beta FC",
                        Score = "3:1",
                        BTTSLabel = true,
                        IsLive = false
                    }
                ]
            });

        await service.RunScoreUpdaterAsync();

        var prediction = await context.Predictions.SingleAsync();
        Assert.Equal("3:1", prediction.ActualScore);
        Assert.Equal("Home Win", prediction.ActualOutcome);
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
        var kickoffUtc = kickoff.Kind == DateTimeKind.Utc
            ? kickoff
            : DateTimeProvider.ConvertLocalToUtc(DateTime.SpecifyKind(kickoff, DateTimeKind.Unspecified));
        var kickoffLocal = kickoff.Kind == DateTimeKind.Utc
            ? DateTimeProvider.ConvertUtcToLocal(kickoff)
            : kickoff;

        return new Prediction
        {
            Date = date,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoffLocal),
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = $"{league.Trim().ToLowerInvariant()}|{homeTeam.Trim().ToLowerInvariant()}|{awayTeam.Trim().ToLowerInvariant()}",
            League = league,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            PredictionCategory = predictionCategory,
            PredictedOutcome = predictedOutcome,
            IsLive = false
        };
    }

    private static ForecastObservation CreateForecast(
        string date,
        DateTime kickoff,
        string homeTeam,
        string awayTeam,
        PredictionMarket market,
        string predictedOutcome,
        string league = "Spain LaLiga")
    {
        var kickoffUtc = kickoff.Kind == DateTimeKind.Utc
            ? kickoff
            : DateTimeProvider.ConvertLocalToUtc(DateTime.SpecifyKind(kickoff, DateTimeKind.Unspecified));
        var kickoffLocal = kickoff.Kind == DateTimeKind.Utc
            ? DateTimeProvider.ConvertUtcToLocal(kickoff)
            : kickoff;

        return new ForecastObservation
        {
            Date = date,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoffLocal),
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = $"{league.Trim().ToLowerInvariant()}|{homeTeam.Trim().ToLowerInvariant()}|{awayTeam.Trim().ToLowerInvariant()}",
            League = league,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            Market = market,
            PredictedOutcome = predictedOutcome,
            IsLive = false,
            IsSettled = false
        };
    }

    private static DateTime GetFutureKickoffForToday(DateTime nowLocal)
    {
        var candidate = nowLocal.AddHours(4);
        if (candidate.Date == nowLocal.Date)
        {
            return candidate;
        }

        candidate = nowLocal.AddMinutes(1);
        if (candidate.Date == nowLocal.Date)
        {
            return candidate;
        }

        return nowLocal.AddSeconds(1);
    }

    private static DateTime GetStartedKickoffForTodayOrYesterday(int hour, int minute = 0)
    {
        var nowLocal = DateTimeProvider.GetLocalTime();
        var todayKickoff = nowLocal.Date.AddHours(hour).AddMinutes(minute);
        return todayKickoff <= nowLocal ? todayKickoff : todayKickoff.AddDays(-1);
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
        var kickoffUtc = kickoff.Kind == DateTimeKind.Utc
            ? kickoff
            : DateTimeProvider.ConvertLocalToUtc(DateTime.SpecifyKind(kickoff, DateTimeKind.Unspecified));
        var kickoffLocal = kickoff.Kind == DateTimeKind.Utc
            ? DateTimeProvider.ConvertUtcToLocal(kickoff)
            : kickoff;

        return new Prediction
        {
            Date = date,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoffLocal),
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = $"{league.Trim().ToLowerInvariant()}|{homeTeam.Trim().ToLowerInvariant()}|{awayTeam.Trim().ToLowerInvariant()}",
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

    private static AnalyzerService CreateAnalyzerService(
        ApplicationDbContext context,
        StubWebScraperService scraper,
        AiScoreSourceHealthTracker? aiScoreSourceHealthTracker = null,
        SofaScoreSourceHealthTracker? sofaScoreSourceHealthTracker = null)
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
            aiScoreSourceHealthTracker ?? new AiScoreSourceHealthTracker(),
            sofaScoreSourceHealthTracker ?? new SofaScoreSourceHealthTracker(),
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                OverTwoGoalsStrongThreshold = 0.58,
                DrawStrongThreshold = 0.30,
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
        public IReadOnlyList<PredictionCandidate> UnderTwoGoals(IEnumerable<MatchData> matches) => [];
        public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches) => [];
    }

    private sealed class StubWebScraperService : IWebScraperService
    {
        public List<MatchScore> MatchScores { get; init; } = [];
        public List<AiScoreMatchScore> AiScoreMatchScores { get; init; } = [];
        public List<SofaScoreMatchScore> SofaScoreMatchScores { get; init; } = [];

        public Task ScrapeMatchDataAsync() => Task.CompletedTask;
        public Task<List<MatchScore>> ScrapeMatchScoresAsync() => Task.FromResult(MatchScores);
        public Task<List<MatchScore>> ScrapeTennisScoresMatchScoresAsync() => Task.FromResult(new List<MatchScore>());
        public Task<List<AiScoreMatchScore>> ScrapeAiScoreMatchScoresAsync() => Task.FromResult(AiScoreMatchScores);
        public Task<List<SofaScoreMatchScore>> ScrapeSofaScoreMatchScoresAsync(IEnumerable<SofaScoreFixtureRequest> fixtures) => Task.FromResult(SofaScoreMatchScores);
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
