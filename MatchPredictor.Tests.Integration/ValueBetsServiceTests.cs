using System.Text.Json;
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

public class ValueBetsServiceTests
{
    [Fact]
    public async Task GetTopValueBetsAsync_UsesDeterministicEdgeFiltering_AndDoesNotCrossWireAiJustifications()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetUpcomingKickoffForToday(2);
        var date = DateTimeProvider.GetLocalDate().ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.Add(new MatchData
        {
            Date = date,
            Time = time,
            MatchLocalDate = DateTimeProvider.GetLocalDate(),
            MatchLocalTime = TimeOnly.FromDateTime(kickoff),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            FixtureKey = "test-league|alpha|beta",
            League = "Test League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            HomeWin = 0.60,
            Draw = 0.25,
            AwayWin = 0.15,
            OverTwoGoals = 0.52,
            UnderTwoGoals = 0.48
        });

        await context.SaveChangesAsync();

        var analyzer = new FakeDataAnalyzerService();
        analyzer.Seed(
            "Alpha",
            "Beta",
            [
                CreateCandidate(PredictionMarket.BothTeamsScore, "BothTeamsScore", "BTTS", 0.80, 0.80),
                CreateCandidate(PredictionMarket.Over25Goals, "Over2.5Goals", "Over 2.5", 0.52, 0.60, "Beta"),
                CreateCandidate(PredictionMarket.Under25Goals, "Under2.5Goals", "Under 2.5", 0.48, 0.64),
                CreateCandidate(PredictionMarket.HomeWin, "StraightWin", "Home Win", 0.60, 0.69),
                CreateCandidate(PredictionMarket.AwayWin, "StraightWin", "Away Win", 0.15, 0.18)
            ]);

        var service = new ValueBetsService(
            context,
            analyzer,
            new FakeThresholdTuningService
            {
                Decisions =
                {
                    [PredictionMarket.Over25Goals] = new ThresholdDecision { Threshold = 0.58, ThresholdSource = "Tuned" },
                    [PredictionMarket.Under25Goals] = new ThresholdDecision { Threshold = 0.58, ThresholdSource = "Configured" },
                    [PredictionMarket.HomeWin] = new ThresholdDecision { Threshold = 0.68, ThresholdSource = "Configured" },
                }
            },
            new FakeAiAdvisorService(payload =>
            {
                using var document = JsonDocument.Parse(payload);
                var picks = document.RootElement.GetProperty("Picks").EnumerateArray().ToList();
                var totalsPick = picks.Single(pick =>
                    pick.GetProperty("PredictionCategory").GetString() == "Under2.5Goals");

                var candidateKey = totalsPick.GetProperty("CandidateKey").GetString();
                return JsonSerializer.Serialize(new
                {
                    picks = new[]
                    {
                        new
                        {
                            CandidateKey = candidateKey,
                            AiJustification = "Calibrated under-goals probability still sits clearly above the market."
                        }
                    }
                });
            }),
            new FakeSourceMarketPricingService(),
            Options.Create(new PredictionSettings
            {
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70,
                OverTwoGoalsStrongThreshold = 0.58,
                UnderTwoGoalsStrongThreshold = 0.58,
                ValueBetMinimumEdge = 0.03
            }),
            NullLogger<ValueBetsService>.Instance);

        var results = (await service.GetTopValueBetsAsync()).ToList();

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, result => result.PredictionCategory == "BothTeamsScore");
        Assert.Equal("Under2.5Goals", results[0].PredictionCategory);
        Assert.True(results[0].ExpectedValuePercent >= results[1].ExpectedValuePercent);

        var under = Assert.Single(results.Where(result => result.PredictionCategory == "Under2.5Goals"));
        Assert.Equal(0.64, under.MathematicalProbability, 3);
        Assert.Equal(0.48, under.MarketProbability, 3);
        Assert.Equal(0.16, under.Edge, 3);
        Assert.Equal(Math.Round(1d / 0.48d, 4), under.DecimalOdds, 4);
        Assert.Equal(BetPricingMath.DefaultKellyFraction, under.KellyFraction, 6);
        Assert.Equal(
            BetPricingMath.CalculateFractionalKellyStakeFraction(under.MathematicalProbability, under.DecimalOdds),
            under.KellyStakeFraction,
            6);
        Assert.True(under.KellyStakeFraction > 0);
        Assert.Equal("Calibrated under-goals probability still sits clearly above the market.", under.AiJustification);

        Assert.DoesNotContain(results, result => result.PredictionCategory == "Over2.5Goals");

        var homeWin = Assert.Single(results.Where(result => result.PredictionCategory == "StraightWin"));
        Assert.Equal("Home Win", homeWin.PredictedOutcome);
        Assert.Equal(0.69, homeWin.MathematicalProbability, 3);
        Assert.Equal(0.60, homeWin.MarketProbability, 3);
        Assert.Equal(0.09, homeWin.Edge, 3);
        Assert.Equal(Math.Round(1d / 0.60d, 4), homeWin.DecimalOdds, 4);
        Assert.Contains("EV", homeWin.AiJustification);
    }

    [Fact]
    public async Task GetTopValueBetsAsync_FallsBackToDeterministicResults_WhenAiServiceReturnsWarning()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetUpcomingKickoffForToday(3);
        var date = DateTimeProvider.GetLocalDate().ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.Add(new MatchData
        {
            Date = date,
            Time = time,
            MatchLocalDate = DateTimeProvider.GetLocalDate(),
            MatchLocalTime = TimeOnly.FromDateTime(kickoff),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            FixtureKey = "test-league|gamma|delta",
            League = "Test League",
            HomeTeam = "Gamma",
            AwayTeam = "Delta",
            HomeWin = 0.44,
            Draw = 0.28,
            AwayWin = 0.28,
            OverTwoGoals = 0.54,
            UnderTwoGoals = 0.46
        });

        await context.SaveChangesAsync();

        var analyzer = new FakeDataAnalyzerService();
        analyzer.Seed(
            "Gamma",
            "Delta",
            [
                CreateCandidate(PredictionMarket.Over25Goals, "Over2.5Goals", "Over 2.5", 0.54, 0.61, homeTeam: "Gamma", awayTeam: "Delta"),
                CreateCandidate(PredictionMarket.Under25Goals, "Under2.5Goals", "Under 2.5", 0.46, 0.60, homeTeam: "Gamma", awayTeam: "Delta"),
                CreateCandidate(PredictionMarket.HomeWin, "StraightWin", "Home Win", 0.44, 0.49, homeTeam: "Gamma", awayTeam: "Delta"),
                CreateCandidate(PredictionMarket.AwayWin, "StraightWin", "Away Win", 0.28, 0.30, homeTeam: "Gamma", awayTeam: "Delta")
            ]);

        var service = new ValueBetsService(
            context,
            analyzer,
            new FakeThresholdTuningService
            {
                Decisions =
                {
                    [PredictionMarket.Over25Goals] = new ThresholdDecision { Threshold = 0.58, ThresholdSource = "Configured" },
                    [PredictionMarket.Under25Goals] = new ThresholdDecision { Threshold = 0.58, ThresholdSource = "Configured" }
                }
            },
            new FakeAiAdvisorService(_ => "⚠️ busy"),
            new FakeSourceMarketPricingService(),
            Options.Create(new PredictionSettings
            {
                OverTwoGoalsStrongThreshold = 0.58,
                UnderTwoGoalsStrongThreshold = 0.58,
                ValueBetMinimumEdge = 0.03
            }),
            NullLogger<ValueBetsService>.Instance);

        var results = (await service.GetTopValueBetsAsync()).ToList();

        var under = Assert.Single(results);
        Assert.Equal("Under2.5Goals", under.PredictionCategory);
        Assert.Equal("Under 2.5", under.PredictedOutcome);
        Assert.Contains("model 60.0% vs market 46.0%", under.AiJustification);
    }

    [Fact]
    public async Task GetTopValueBetsAsync_IncludesBtts_WhenLiveSourcePricingProvidesThatMarket()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetUpcomingKickoffForToday(4);
        var date = DateTimeProvider.GetLocalDate().ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.Add(new MatchData
        {
            Date = date,
            Time = time,
            MatchLocalDate = DateTimeProvider.GetLocalDate(),
            MatchLocalTime = TimeOnly.FromDateTime(kickoff),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            FixtureKey = "england-premier-league|alpha-fc|beta-united",
            League = "England - Premier League",
            HomeTeam = "Alpha FC",
            AwayTeam = "Beta United",
            HomeWin = 0.51,
            Draw = 0.26,
            AwayWin = 0.23,
            OverTwoGoals = 0.54,
            UnderTwoGoals = 0.46
        });

        await context.SaveChangesAsync();

        var analyzer = new FakeDataAnalyzerService();
        analyzer.Seed(
            "Alpha FC",
            "Beta United",
            [
                CreateCandidate(PredictionMarket.BothTeamsScore, "BothTeamsScore", "BTTS", 0.57, 0.63, homeTeam: "Alpha FC", awayTeam: "Beta United"),
                CreateCandidate(PredictionMarket.Over25Goals, "Over2.5Goals", "Over 2.5", 0.54, 0.56, homeTeam: "Alpha FC", awayTeam: "Beta United")
            ]);

        var service = new ValueBetsService(
            context,
            analyzer,
            new FakeThresholdTuningService
            {
                Decisions =
                {
                    [PredictionMarket.BothTeamsScore] = new ThresholdDecision { Threshold = 0.55, ThresholdSource = "Configured" }
                }
            },
            new FakeAiAdvisorService(_ => "{\"picks\":[]}"),
            new FakeSourceMarketPricingService
            {
                Fixtures =
                [
                    new SourceMarketFixture
                    {
                        League = "England - Premier League",
                        HomeTeam = "Alpha FC",
                        AwayTeam = "Beta United",
                        MatchTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoff),
                        BttsYesProbability = 0.55,
                        BttsYesOdds = 1.85,
                        BttsNoProbability = 0.45
                    }
                ]
            },
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55,
                ValueBetMinimumEdge = 0.03
            }),
            NullLogger<ValueBetsService>.Instance);

        var results = (await service.GetTopValueBetsAsync()).ToList();

        var btts = Assert.Single(results);
        Assert.Equal("BothTeamsScore", btts.PredictionCategory);
        Assert.Equal("BTTS", btts.PredictedOutcome);
        Assert.Equal(0.63, btts.MathematicalProbability, 3);
        Assert.Equal(BetPricingMath.ConvertDecimalOddsToProbability(1.85) ?? 0d, btts.MarketProbability, 5);
        Assert.Equal(btts.ImpliedProbability, btts.MarketProbability, 5);
        Assert.Equal(Math.Round(0.63 - (BetPricingMath.ConvertDecimalOddsToProbability(1.85) ?? 0d), 6), Math.Round(btts.Edge, 6), 6);
        Assert.Equal(1.85, btts.DecimalOdds, 2);
        Assert.Equal("Source decimal odds", btts.OddsDerivationSource);
    }

    [Fact]
    public async Task GetTopValueBetsAsync_RanksByExpectedValue_WhenLiveRawOddsAreAvailable()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetUpcomingKickoffForToday(4);
        var date = DateTimeProvider.GetLocalDate().ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.AddRange(
            new MatchData
            {
                Date = date,
                Time = time,
                MatchLocalDate = DateTimeProvider.GetLocalDate(),
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
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
                Date = date,
                Time = time,
                MatchLocalDate = DateTimeProvider.GetLocalDate(),
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff.AddMinutes(30)),
                FixtureKey = "league|gamma|delta",
                League = "League",
                HomeTeam = "Gamma",
                AwayTeam = "Delta",
                HomeWin = 0.65,
                Draw = 0.20,
                AwayWin = 0.15
            });

        await context.SaveChangesAsync();

        var analyzer = new FakeDataAnalyzerService();
        analyzer.Seed("Alpha", "Beta", [CreateCandidate(PredictionMarket.HomeWin, "StraightWin", "Home Win", 0.55, 0.60, homeTeam: "Alpha", awayTeam: "Beta")]);
        analyzer.Seed("Gamma", "Delta", [CreateCandidate(PredictionMarket.HomeWin, "StraightWin", "Home Win", 0.65, 0.75, homeTeam: "Gamma", awayTeam: "Delta")]);

        var service = new ValueBetsService(
            context,
            analyzer,
            new FakeThresholdTuningService
            {
                Decisions =
                {
                    [PredictionMarket.HomeWin] = new ThresholdDecision { Threshold = 0.55, ThresholdSource = "Configured" }
                }
            },
            new FakeAiAdvisorService(_ => "{\"picks\":[]}"),
            new FakeSourceMarketPricingService
            {
                Fixtures =
                [
                    new SourceMarketFixture
                    {
                        League = "League",
                        HomeTeam = "Alpha",
                        AwayTeam = "Beta",
                        MatchTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoff),
                        HomeWinProbability = 0.55,
                        HomeWinOdds = 2.20
                    },
                    new SourceMarketFixture
                    {
                        League = "League",
                        HomeTeam = "Gamma",
                        AwayTeam = "Delta",
                        MatchTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoff.AddMinutes(30)),
                        HomeWinProbability = 0.65,
                        HomeWinOdds = 1.55
                    }
                ]
            },
            Options.Create(new PredictionSettings
            {
                HomeWinStrong = 0.55,
                ValueBetMinimumEdge = 0.03
            }),
            NullLogger<ValueBetsService>.Instance);

        var results = (await service.GetTopValueBetsAsync()).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alpha", results[0].HomeTeam);
        Assert.True(results[0].ExpectedValuePercent > results[1].ExpectedValuePercent);
        Assert.Equal(2.20, results[0].DecimalOdds, 2);
        Assert.Equal(0.454545, results[0].ImpliedProbability, 5);
        Assert.Equal(results[0].ImpliedProbability, results[0].MarketProbability, 5);
        Assert.Equal("Source decimal odds", results[0].OddsDerivationSource);
    }

    [Fact]
    public async Task GetTopValueBetsAsync_UsesOddsImpliedProbabilityForEdgeFiltering_WhenRawOddsExist()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetUpcomingKickoffForToday(5);
        var date = DateTimeProvider.GetLocalDate().ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.Add(new MatchData
        {
            Date = date,
            Time = time,
            MatchLocalDate = DateTimeProvider.GetLocalDate(),
            MatchLocalTime = TimeOnly.FromDateTime(kickoff),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            FixtureKey = "league|priced|edge-case",
            League = "League",
            HomeTeam = "Priced",
            AwayTeam = "Edge Case",
            HomeWin = 0.54,
            Draw = 0.24,
            AwayWin = 0.22
        });

        await context.SaveChangesAsync();

        var analyzer = new FakeDataAnalyzerService();
        analyzer.Seed(
            "Priced",
            "Edge Case",
            [
                CreateCandidate(
                    PredictionMarket.HomeWin,
                    "StraightWin",
                    "Home Win",
                    0.54,
                    0.58,
                    homeTeam: "Priced",
                    awayTeam: "Edge Case")
            ]);

        var service = new ValueBetsService(
            context,
            analyzer,
            new FakeThresholdTuningService
            {
                Decisions =
                {
                    [PredictionMarket.HomeWin] = new ThresholdDecision { Threshold = 0.55, ThresholdSource = "Configured" }
                }
            },
            new FakeAiAdvisorService(_ => "{\"picks\":[]}"),
            new FakeSourceMarketPricingService
            {
                Fixtures =
                [
                    new SourceMarketFixture
                    {
                        League = "League",
                        HomeTeam = "Priced",
                        AwayTeam = "Edge Case",
                        MatchTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoff),
                        HomeWinProbability = 0.54,
                        HomeWinOdds = 1.75
                    }
                ]
            },
            Options.Create(new PredictionSettings
            {
                HomeWinStrong = 0.55,
                ValueBetMinimumEdge = 0.02
            }),
            NullLogger<ValueBetsService>.Instance);

        var results = (await service.GetTopValueBetsAsync()).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetValueBetReportAsync_ReturnsExclusionBreakdown_AndPricingFreshness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetUpcomingKickoffForToday(2);
        var date = DateTimeProvider.GetLocalDate().ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.Add(new MatchData
        {
            Date = date,
            Time = time,
            MatchLocalDate = DateTimeProvider.GetLocalDate(),
            MatchLocalTime = TimeOnly.FromDateTime(kickoff),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            FixtureKey = "test-league|alpha|beta",
            League = "Test League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            HomeWin = 0.60,
            Draw = 0.25,
            AwayWin = 0.15,
            OverTwoGoals = 0.52,
            UnderTwoGoals = 0.48,
            BttsYes = 0.56,
            BttsNo = 0.44
        });

        await context.SaveChangesAsync();

        var analyzer = new FakeDataAnalyzerService();
        analyzer.Seed(
            "Alpha",
            "Beta",
            [
                CreateCandidate(PredictionMarket.HomeWin, "StraightWin", "Home Win", 0.60, 0.69),
                CreateCandidate(PredictionMarket.Over25Goals, "Over2.5Goals", "Over 2.5", 0.52, 0.54),
                CreateCandidate(PredictionMarket.AwayWin, "StraightWin", "Away Win", 0.15, 0.17)
            ]);

        var service = new ValueBetsService(
            context,
            analyzer,
            new FakeThresholdTuningService
            {
                Decisions =
                {
                    [PredictionMarket.HomeWin] = new ThresholdDecision { Threshold = 0.68, ThresholdSource = "Configured" },
                    [PredictionMarket.Over25Goals] = new ThresholdDecision { Threshold = 0.58, ThresholdSource = "Configured" },
                    [PredictionMarket.AwayWin] = new ThresholdDecision { Threshold = 0.16, ThresholdSource = "Configured" }
                }
            },
            new FakeAiAdvisorService(_ => "{\"picks\":[]}"),
            new FakeSourceMarketPricingService(),
            Options.Create(new PredictionSettings
            {
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70,
                OverTwoGoalsStrongThreshold = 0.58,
                ValueBetMinimumEdge = 0.03
            }),
            NullLogger<ValueBetsService>.Instance);

        var report = await service.GetValueBetReportAsync();

        Assert.Equal(3, report.ConsideredCandidateCount);
        Assert.Single(report.Bets);
        Assert.Equal("Stored sync snapshot", report.Bets[0].PricingSource);
        Assert.Contains("latest stored sync pricing", report.Bets[0].OddsFreshness, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Derived from stored sync probability", report.Bets[0].OddsDerivationSource);
        Assert.Contains("Model 69.0% vs market 60.0%", report.Bets[0].EdgeSource);
        Assert.Contains(report.ExclusionBreakdown, item => item.Key == "below_threshold" && item.Count == 1);
        Assert.Contains(report.ExclusionBreakdown, item => item.Key == "insufficient_edge" && item.Count == 1);
    }

    [Fact]
    public async Task GetValueBetReportAsync_SkipsBrokenFixture_AndReturnsRemainingValueBets()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = GetUpcomingKickoffForToday(2);
        var date = DateTimeProvider.GetLocalDate().ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.AddRange(
            new MatchData
            {
                Date = date,
                Time = time,
                MatchLocalDate = DateTimeProvider.GetLocalDate(),
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
                FixtureKey = "test-league|alpha|beta",
                League = "Test League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                HomeWin = 0.60,
                Draw = 0.25,
                AwayWin = 0.15,
                OverTwoGoals = 0.52,
                UnderTwoGoals = 0.48
            },
            new MatchData
            {
                Date = date,
                Time = time,
                MatchLocalDate = DateTimeProvider.GetLocalDate(),
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
                FixtureKey = "test-league|broken|fixture",
                League = "Test League",
                HomeTeam = "Broken",
                AwayTeam = "Fixture",
                HomeWin = 0.51,
                Draw = 0.26,
                AwayWin = 0.23,
                OverTwoGoals = 0.53,
                UnderTwoGoals = 0.47
            });

        await context.SaveChangesAsync();

        var analyzer = new FakeDataAnalyzerService();
        analyzer.Seed(
            "Alpha",
            "Beta",
            [
                CreateCandidate(PredictionMarket.HomeWin, "StraightWin", "Home Win", 0.60, 0.69)
            ]);
        analyzer.SeedException("Broken", "Fixture", new InvalidOperationException("bad fixture"));

        var service = new ValueBetsService(
            context,
            analyzer,
            new FakeThresholdTuningService
            {
                Decisions =
                {
                    [PredictionMarket.HomeWin] = new ThresholdDecision { Threshold = 0.68, ThresholdSource = "Configured" }
                }
            },
            new FakeAiAdvisorService(_ => "{\"picks\":[]}"),
            new FakeSourceMarketPricingService(),
            Options.Create(new PredictionSettings
            {
                HomeWinStrong = 0.68,
                ValueBetMinimumEdge = 0.03
            }),
            NullLogger<ValueBetsService>.Instance);

        var report = await service.GetValueBetReportAsync();

        Assert.Single(report.Bets);
        Assert.Contains(report.Warnings, warning => warning.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.ExclusionBreakdown, item => item.Key == "processing_error" && item.Count == 1);
    }

    private static PredictionCandidate CreateCandidate(
        PredictionMarket market,
        string category,
        string outcome,
        double rawProbability,
        double calibratedProbability,
        string calibratorUsed = "Bucket",
        string homeTeam = "Alpha",
        string awayTeam = "Beta")
    {
        return new PredictionCandidate
        {
            Market = market,
            Date = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.GetLocalTime().AddHours(2).ToString("HH:mm"),
            League = "Test League",
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            PredictionCategory = category,
            PredictedOutcome = outcome,
            RawProbability = rawProbability,
            CalibratedProbability = calibratedProbability,
            CalibratorUsed = calibratorUsed
        };
    }

    private static DateTime GetUpcomingKickoffForToday(int preferredOffsetHours)
    {
        var now = DateTimeProvider.GetLocalTime();
        var kickoff = now.AddHours(preferredOffsetHours);

        if (kickoff.Date == now.Date)
        {
            return kickoff;
        }

        // Keep test fixtures in today's card even when the suite runs late at night.
        return now.AddMinutes(10);
    }

    private sealed class FakeDataAnalyzerService : IDataAnalyzerService
    {
        private readonly Dictionary<string, IReadOnlyList<PredictionCandidate>> _candidates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _exceptions = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(string homeTeam, string awayTeam, IReadOnlyList<PredictionCandidate> candidates)
        {
            _candidates[BuildKey(homeTeam, awayTeam)] = candidates;
        }

        public void SeedException(string homeTeam, string awayTeam, Exception exception)
        {
            _exceptions[BuildKey(homeTeam, awayTeam)] = exception;
        }

        public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches)
        {
            var match = Assert.Single(matches);
            if (_exceptions.TryGetValue(BuildKey(match.HomeTeam ?? string.Empty, match.AwayTeam ?? string.Empty), out var exception))
            {
                throw exception;
            }

            return _candidates[BuildKey(match.HomeTeam ?? string.Empty, match.AwayTeam ?? string.Empty)];
        }

        public IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates) =>
            forecastCandidates.ToList();

        public IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches) => BuildForecastCandidates(matches);
        public IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches) => BuildForecastCandidates(matches);
        public IReadOnlyList<PredictionCandidate> UnderTwoGoals(IEnumerable<MatchData> matches) => BuildForecastCandidates(matches);
        public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches) => BuildForecastCandidates(matches);

        private static string BuildKey(string homeTeam, string awayTeam) =>
            $"{homeTeam}|{awayTeam}";
    }

    private sealed class FakeThresholdTuningService : IThresholdTuningService
    {
        public Dictionary<PredictionMarket, ThresholdDecision> Decisions { get; } = new();

        public double GetThreshold(PredictionMarket market, double fallbackThreshold, string? league = null) =>
            GetThresholdDecision(market, fallbackThreshold, league).Threshold;

        public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold, string? league = null)
        {
            if (Decisions.TryGetValue(market, out var decision))
            {
                return decision;
            }

            return new ThresholdDecision
            {
                Threshold = fallbackThreshold,
                ThresholdSource = "Configured"
            };
        }

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }

    private sealed class FakeAiAdvisorService : IAiAdvisorService
    {
        private readonly Func<string, string> _responseFactory;

        public FakeAiAdvisorService(Func<string, string> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public Task<AiChatResponse> GetAdviceAsync(string userPrompt, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(new AiChatResponse());

        public Task<string> AnalyzeValueBetsAsync(string payload, CancellationToken ct = default) =>
            Task.FromResult(_responseFactory(payload));
    }

    private sealed class FakeSourceMarketPricingService : ISourceMarketPricingService
    {
        public IReadOnlyList<SourceMarketFixture> Fixtures { get; init; } = [];

        public Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default) =>
            Task.FromResult(Fixtures);
    }
}
