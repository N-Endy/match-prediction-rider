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

public class BetslipGenerationServiceBankerTests
{
    [Fact]
    public async Task GenerateDailyBetslipsAsync_ProducesBankerSlip_WithValidOddsProduct()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var today = DateTimeProvider.GetLocalDate();
        var kickoff = DateTime.UtcNow.AddHours(5);
        var fixtures = new List<SourceMarketFixture>();
        var predictions = new List<Prediction>();

        for (var i = 1; i <= 6; i++)
        {
            var home = $"BankerHome{i}";
            var away = $"BankerAway{i}";
            var fixtureKey = $"banker-fx-{i}";
            var matchKickoff = kickoff.AddMinutes(i * 10);
            predictions.Add(new Prediction
            {
                Id = i,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = matchKickoff,
                League = "Test League",
                HomeTeam = home,
                AwayTeam = away,
                FixtureKey = fixtureKey,
                PredictionCategory = i % 2 == 0 ? "BothTeamsScore" : "Over2.5Goals",
                PredictedOutcome = i % 2 == 0 ? "BTTS" : "Over 2.5",
                ConfidenceScore = 0.80m - i * 0.01m,
                WasPublished = true,
                IsCurrentRevision = true,
                PredictionRunId = Guid.NewGuid()
            });

            fixtures.Add(new SourceMarketFixture
            {
                EventId = $"evt-{i}",
                League = "Test League",
                HomeTeam = home,
                AwayTeam = away,
                MatchTimeUtc = matchKickoff,
                BttsYesOdds = 1.55,
                Over25Odds = 1.60,
                Under25Odds = 2.20,
                HomeWinOdds = 1.70,
                AwayWinOdds = 4.50,
                DrawOdds = 3.40
            });
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var booking = new FakeBookingService();
        var pricing = new FakePricingService { Fixtures = fixtures };
        var ai = new FakeBankerAiAdvisor
        {
            ResultFactory = candidates => new BankerPickResult
            {
                Picks = candidates.Take(4).Select(c => new BetslipDrawPickSelection
                {
                    PredictionId = c.PredictionId,
                    Reason = "Solid alignment"
                }).ToList(),
                RiskNote = "High stakes — verify lineups."
            }
        };

        var settings = Options.Create(new BetslipSettings
        {
            BookingDelayMilliseconds = 0,
            BankerMinConfidence = 0.60,
            BankerMinOdds = 5.0,
            BankerMaxOdds = 10.0,
            BankerFallbackMinOdds = 4.0,
            BankerFallbackMaxOdds = 12.0,
            BankerShortlistSize = 20,
            BankerMaxPicks = 8,
            MaxSelectionsPerSlip = 50
        });

        var service = new BetslipGenerationService(
            context,
            booking,
            pricing,
            ai,
            settings,
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.IsCurrent);

        var banker = Assert.Single(set.Slips, s => s.SlipNumber == BetslipGenerationService.BankerSlipNumber);
        Assert.Equal("Banker of the Day", banker.Title);
        Assert.True(banker.CombinedDecimalOdds is >= 5.0 and <= 10.0);
        Assert.Equal("High stakes — verify lineups.", banker.AiSummary);
        Assert.NotEmpty(banker.BookingCode);
        Assert.All(banker.Selections, s => Assert.True(s.DecimalOdds is > 1));
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_FallsBackToDeterministic_WhenAiReturnsJunk()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var today = DateTimeProvider.GetLocalDate();
        var kickoff = DateTime.UtcNow.AddHours(4);

        for (var i = 1; i <= 5; i++)
        {
            var matchKickoff = kickoff.AddMinutes(i * 15);
            context.Predictions.Add(new Prediction
            {
                Id = 100 + i,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = matchKickoff,
                League = "Fallback League",
                HomeTeam = $"FbHome{i}",
                AwayTeam = $"FbAway{i}",
                FixtureKey = $"fb-fx-{i}",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ConfidenceScore = 0.78m,
                WasPublished = true,
                IsCurrentRevision = true,
                PredictionRunId = Guid.NewGuid()
            });
        }

        await context.SaveChangesAsync();

        var fixtures = Enumerable.Range(1, 5).Select(i => new SourceMarketFixture
        {
            EventId = $"fb-evt-{i}",
            League = "Fallback League",
            HomeTeam = $"FbHome{i}",
            AwayTeam = $"FbAway{i}",
            MatchTimeUtc = kickoff.AddMinutes(i * 15),
            BttsYesOdds = 1.55
        }).ToList();

        var service = new BetslipGenerationService(
            context,
            new FakeBookingService(),
            new FakePricingService { Fixtures = fixtures },
            new FakeBankerAiAdvisor { ResultFactory = _ => new BankerPickResult() },
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                BankerMinConfidence = 0.60,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0
            }),
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("midday");

        var banker = await context.Betslips
            .Include(s => s.Selections)
            .SingleAsync(s => s.SlipNumber == BetslipGenerationService.BankerSlipNumber);

        Assert.Contains("not AI-vetted", banker.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(banker.CombinedDecimalOdds is >= 4.0 and <= 12.0);
        Assert.NotEmpty(banker.Selections);
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_SendsFullEligiblePool_NotConfidenceShortlistOf20()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var today = DateTimeProvider.GetLocalDate();
        var kickoff = DateTime.UtcNow.AddHours(5);
        var fixtures = new List<SourceMarketFixture>();
        var predictions = new List<Prediction>();

        for (var i = 1; i <= 25; i++)
        {
            var matchKickoff = kickoff.AddMinutes(i * 10);
            predictions.Add(new Prediction
            {
                Id = i,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = matchKickoff,
                League = "Test League",
                HomeTeam = $"PoolHome{i}",
                AwayTeam = $"PoolAway{i}",
                FixtureKey = $"pool-fx-{i}",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ConfidenceScore = 0.90m - i * 0.001m,
                WasPublished = true,
                IsCurrentRevision = true,
                PredictionRunId = Guid.NewGuid()
            });
            fixtures.Add(new SourceMarketFixture
            {
                EventId = $"pool-evt-{i}",
                League = "Test League",
                HomeTeam = $"PoolHome{i}",
                AwayTeam = $"PoolAway{i}",
                MatchTimeUtc = matchKickoff,
                BttsYesOdds = 1.55
            });
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var ai = new FakeBankerAiAdvisor
        {
            ResultFactory = candidates => new BankerPickResult
            {
                Picks = candidates.Skip(20).Take(4).Select(c => new BetslipDrawPickSelection
                {
                    PredictionId = c.PredictionId,
                    Reason = "Research promoted"
                }).ToList(),
                RiskNote = "AI picked beyond the old top 20."
            }
        };

        var service = new BetslipGenerationService(
            context,
            new FakeBookingService(),
            new FakePricingService { Fixtures = fixtures },
            ai,
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                BankerMinConfidence = 0.60,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0,
                BankerShortlistSize = 20,
                BankerMaxPicks = 8
            }),
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("morning");

        Assert.NotNull(ai.LastCandidates);
        Assert.Equal(25, ai.LastCandidates.Count);

        var banker = await context.Betslips
            .Include(s => s.Selections)
            .SingleAsync(s => s.SlipNumber == BetslipGenerationService.BankerSlipNumber);

        Assert.Equal(4, banker.Selections.Count);
        Assert.All(banker.Selections, s => Assert.True(s.PredictionId is >= 21));
        Assert.True(banker.CombinedDecimalOdds is >= 5.0 and <= 10.0);
        Assert.Equal("AI picked beyond the old top 20.", banker.AiSummary);
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_CapsBankerAiPoolAtForty()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var today = DateTimeProvider.GetLocalDate();
        var kickoff = DateTime.UtcNow.AddHours(5);
        var fixtures = new List<SourceMarketFixture>();

        for (var i = 1; i <= 45; i++)
        {
            var matchKickoff = kickoff.AddMinutes(i * 5);
            context.Predictions.Add(new Prediction
            {
                Id = i,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = matchKickoff,
                League = "Cap League",
                HomeTeam = $"CapHome{i}",
                AwayTeam = $"CapAway{i}",
                FixtureKey = $"cap-fx-{i}",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ConfidenceScore = 0.90m - i * 0.001m,
                WasPublished = true,
                IsCurrentRevision = true,
                PredictionRunId = Guid.NewGuid()
            });
            fixtures.Add(new SourceMarketFixture
            {
                EventId = $"cap-evt-{i}",
                League = "Cap League",
                HomeTeam = $"CapHome{i}",
                AwayTeam = $"CapAway{i}",
                MatchTimeUtc = matchKickoff,
                BttsYesOdds = 1.55
            });
        }

        await context.SaveChangesAsync();

        var ai = new FakeBankerAiAdvisor
        {
            ResultFactory = candidates => new BankerPickResult
            {
                Picks = candidates.Take(4).Select(c => new BetslipDrawPickSelection { PredictionId = c.PredictionId }).ToList(),
                RiskNote = "capped"
            }
        };

        var service = new BetslipGenerationService(
            context,
            new FakeBookingService(),
            new FakePricingService { Fixtures = fixtures },
            ai,
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                BankerMinConfidence = 0.60,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0,
                BankerShortlistSize = 20
            }),
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("morning");

        Assert.NotNull(ai.LastCandidates);
        Assert.Equal(BetslipGenerationService.MaxResearchPoolSize, ai.LastCandidates.Count);
        Assert.DoesNotContain(ai.LastCandidates, c => c.PredictionId > 40);
    }

    private sealed class FakeBookingService : ISportyBetBookingService
    {
        public Task<BookingResult> BookGamesAsync(List<BookingSelection> selections) =>
            Task.FromResult(new BookingResult
            {
                Success = true,
                BookingCode = "BANKER1",
                BookingUrl = "https://www.sportybet.com/ng/sport/football?share=" + "BANKER1",
                BookedCount = selections.Count,
                Message = $"Booked {selections.Count}/{selections.Count} games."
            });
    }

    private sealed class FakePricingService : ISourceMarketPricingService
    {
        public IReadOnlyList<SourceMarketFixture> Fixtures { get; init; } = [];

        public Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default) =>
            Task.FromResult(Fixtures);
    }

    private sealed class FakeBankerAiAdvisor : IAiAdvisorService
    {
        public Func<IReadOnlyList<BankerPickRequest>, BankerPickResult> ResultFactory { get; init; } =
            _ => new BankerPickResult();

        public Task<AiChatResponse> GetAdviceAsync(string userPrompt, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(new AiChatResponse());

        public Task<string> AnalyzeValueBetsAsync(string payload, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<BetslipDrawPickSelection>> SelectBestDrawPicksAsync(
            IReadOnlyList<BetslipDrawPickRequest> candidates,
            int count = 5,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BetslipDrawPickSelection>>([]);

        public IReadOnlyList<BankerPickRequest>? LastCandidates { get; private set; }

        public Task<BankerPickResult> SelectBankerPicksAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default)
        {
            LastCandidates = candidates.ToList();
            return Task.FromResult(ResultFactory(candidates));
        }

        public Task<LadderRankResult> RankLadderCandidatesAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderRankResult());
    }
}
