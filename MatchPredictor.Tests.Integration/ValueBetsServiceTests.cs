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
        var kickoff = DateTimeProvider.GetLocalTime().AddHours(2);
        var date = kickoff.ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.Add(new MatchData
        {
            Date = date,
            Time = time,
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
                CreateCandidate(PredictionMarket.HomeWin, "StraightWin", "Home Win", 0.60, 0.69),
                CreateCandidate(PredictionMarket.Draw, "Draw", "Draw", 0.25, 0.33),
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
                    [PredictionMarket.HomeWin] = new ThresholdDecision { Threshold = 0.68, ThresholdSource = "Configured" },
                    [PredictionMarket.Draw] = new ThresholdDecision { Threshold = 0.30, ThresholdSource = "Configured" }
                }
            },
            new FakeAiAdvisorService(payload =>
            {
                using var document = JsonDocument.Parse(payload);
                var picks = document.RootElement.GetProperty("Picks").EnumerateArray().ToList();
                var overPick = picks.Single(pick =>
                    pick.GetProperty("PredictionCategory").GetString() == "Over2.5Goals");

                var candidateKey = overPick.GetProperty("CandidateKey").GetString();
                return JsonSerializer.Serialize(new
                {
                    picks = new[]
                    {
                        new
                        {
                            CandidateKey = candidateKey,
                            AiJustification = "Calibrated over-goals probability still sits clearly above the market."
                        }
                    }
                });
            }),
            new FakeSourceMarketPricingService(),
            Options.Create(new PredictionSettings
            {
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70,
                DrawStrongThreshold = 0.30,
                OverTwoGoalsStrongThreshold = 0.58,
                ValueBetMinimumEdge = 0.03
            }),
            NullLogger<ValueBetsService>.Instance);

        var results = (await service.GetTopValueBetsAsync()).ToList();

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, result => result.PredictionCategory == "BothTeamsScore");
        Assert.DoesNotContain(results, result => result.PredictionCategory == "Draw");

        var homeWin = Assert.Single(results.Where(result =>
            result.PredictionCategory == "StraightWin" && result.PredictedOutcome == "Home Win"));
        Assert.Equal(0.69, homeWin.MathematicalProbability, 3);
        Assert.Equal(0.60, homeWin.MarketProbability, 3);
        Assert.Equal(0.09, homeWin.Edge, 3);
        Assert.Contains("model 69.0% vs market 60.0%", homeWin.AiJustification);

        var over = Assert.Single(results.Where(result => result.PredictionCategory == "Over2.5Goals"));
        Assert.Equal(0.60, over.MathematicalProbability, 3);
        Assert.Equal(0.52, over.MarketProbability, 3);
        Assert.Equal(0.08, over.Edge, 3);
        Assert.Equal("Tuned", over.ThresholdSource);
        Assert.Equal("Calibrated over-goals probability still sits clearly above the market.", over.AiJustification);
    }

    [Fact]
    public async Task GetTopValueBetsAsync_FallsBackToDeterministicResults_WhenAiServiceReturnsWarning()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().AddHours(3);
        var date = kickoff.ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.Add(new MatchData
        {
            Date = date,
            Time = time,
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
                CreateCandidate(PredictionMarket.HomeWin, "StraightWin", "Home Win", 0.44, 0.49, homeTeam: "Gamma", awayTeam: "Delta"),
                CreateCandidate(PredictionMarket.Draw, "Draw", "Draw", 0.28, 0.31, homeTeam: "Gamma", awayTeam: "Delta"),
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
                    [PredictionMarket.Draw] = new ThresholdDecision { Threshold = 0.30, ThresholdSource = "Configured" }
                }
            },
            new FakeAiAdvisorService(_ => "⚠️ busy"),
            new FakeSourceMarketPricingService(),
            Options.Create(new PredictionSettings
            {
                DrawStrongThreshold = 0.30,
                OverTwoGoalsStrongThreshold = 0.58,
                ValueBetMinimumEdge = 0.03
            }),
            NullLogger<ValueBetsService>.Instance);

        var results = (await service.GetTopValueBetsAsync()).ToList();

        var over = Assert.Single(results);
        Assert.Equal("Over2.5Goals", over.PredictionCategory);
        Assert.Equal("Over 2.5", over.PredictedOutcome);
        Assert.Contains("model 61.0% vs market 54.0%", over.AiJustification);
    }

    [Fact]
    public async Task GetTopValueBetsAsync_IncludesBtts_WhenLiveSourcePricingProvidesThatMarket()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTimeProvider.GetLocalTime().AddHours(4);
        var date = kickoff.ToString("dd-MM-yyyy");
        var time = kickoff.ToString("HH:mm");

        context.MatchDatas.Add(new MatchData
        {
            Date = date,
            Time = time,
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
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
                        HomeTeam = "Alpha",
                        AwayTeam = "Beta Utd",
                        MatchTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoff),
                        BttsYesProbability = 0.55,
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
        Assert.Equal(0.55, btts.MarketProbability, 3);
        Assert.Equal(0.08, btts.Edge, 3);
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

    private sealed class FakeDataAnalyzerService : IDataAnalyzerService
    {
        private readonly Dictionary<string, IReadOnlyList<PredictionCandidate>> _candidates = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(string homeTeam, string awayTeam, IReadOnlyList<PredictionCandidate> candidates)
        {
            _candidates[BuildKey(homeTeam, awayTeam)] = candidates;
        }

        public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches)
        {
            var match = Assert.Single(matches);
            return _candidates[BuildKey(match.HomeTeam ?? string.Empty, match.AwayTeam ?? string.Empty)];
        }

        public IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates) =>
            forecastCandidates.ToList();

        public IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches) => BuildForecastCandidates(matches);
        public IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches) => BuildForecastCandidates(matches);
        public IReadOnlyList<PredictionCandidate> Draw(IEnumerable<MatchData> matches) => BuildForecastCandidates(matches);
        public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches) => BuildForecastCandidates(matches);

        private static string BuildKey(string homeTeam, string awayTeam) =>
            $"{homeTeam}|{awayTeam}";
    }

    private sealed class FakeThresholdTuningService : IThresholdTuningService
    {
        public Dictionary<PredictionMarket, ThresholdDecision> Decisions { get; } = new();

        public double GetThreshold(PredictionMarket market, double fallbackThreshold) =>
            GetThresholdDecision(market, fallbackThreshold).Threshold;

        public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold)
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
