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

public class BetslipGenerationServiceLadderPoolTests
{
    [Fact]
    public async Task GenerateDailyBetslipsAsync_LadderIncludesOnePercentEdgePick_ThatBankerSkips()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var today = DateTimeProvider.GetLocalDate();
        var kickoff = DateTime.UtcNow.AddHours(5);
        var fixtures = new List<SourceMarketFixture>();
        var predictions = new List<Prediction>();
        var nextId = 1;

        for (var i = 1; i <= 6; i++)
        {
            AddBttsPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"BankerHome{i}",
                $"BankerAway{i}",
                $"banker-fx-{i}",
                confidence: 0.80m,
                bttsOdds: 1.55);
        }

        for (var i = 1; i <= 12; i++)
        {
            AddBttsPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(100 + i),
                $"ThinHome{i}",
                $"ThinAway{i}",
                $"thin-edge-fx-{i}",
                confidence: 0.66m,
                bttsOdds: 1.55);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var service = new BetslipGenerationService(
            context,
            new FakeBooking(),
            new FakePricing { Fixtures = fixtures },
            new FakeAdvisor(),
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                MaxSlipsPerPrediction = 1,
                BankerMinConfidence = 0.60,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0,
                BankerMaxPicks = 8,
                LadderMinimumEdge = 0.01,
                WeekendSmallSlipCount = 2,
                WeekendMediumSlipCount = 0,
                WeekendBigSlipCount = 0,
                WeekendMegaSlipCount = 0,
                SmallMinOdds = 20,
                SmallMaxOdds = 120,
                SmallFallbackMinOdds = 10,
                SmallFallbackMaxOdds = 150,
                SmallMaxPicks = 18,
                DailyMaxPicks = 18
            }),
            NullLogger<BetslipGenerationService>.Instance,
            Options.Create(new PredictionSettings { ValueBetMinimumEdge = 0.03 }));

        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.IsCurrent);

        var banker = Assert.Single(set.Slips, s => s.SlipNumber == BetslipGenerationService.BankerSlipNumber);
        var ladder = set.Slips
            .Where(s =>
                s.SlipNumber != BetslipGenerationService.BankerSlipNumber &&
                !string.Equals(s.TierLabel, "AI Draws (5)", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(ladder);

        var bankerIds = banker.Selections
            .Select(s => s.PredictionId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        var ladderIds = ladder
            .SelectMany(s => s.Selections)
            .Select(s => s.PredictionId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        var thinIds = predictions
            .Where(p => p.FixtureKey.StartsWith("thin-edge-fx-", StringComparison.Ordinal))
            .Select(p => p.Id)
            .ToHashSet();

        Assert.Empty(bankerIds.Intersect(thinIds));
        Assert.NotEmpty(ladderIds.Intersect(thinIds));
    }

    private static void AddBttsPrediction(
        List<Prediction> predictions,
        List<SourceMarketFixture> fixtures,
        ref int nextId,
        DateOnly today,
        DateTime matchKickoff,
        string home,
        string away,
        string fixtureKey,
        decimal confidence,
        double bttsOdds)
    {
        var id = nextId++;
        predictions.Add(new Prediction
        {
            Id = id,
            Date = today.ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
            MatchLocalDate = today,
            MatchDateTime = matchKickoff,
            League = "Test League",
            HomeTeam = home,
            AwayTeam = away,
            FixtureKey = fixtureKey,
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ConfidenceScore = confidence,
            WasPublished = true,
            IsCurrentRevision = true,
            PredictionRunId = Guid.NewGuid()
        });

        fixtures.Add(new SourceMarketFixture
        {
            EventId = $"evt-{id}",
            League = "Test League",
            HomeTeam = home,
            AwayTeam = away,
            MatchTimeUtc = matchKickoff,
            BttsYesOdds = bttsOdds
        });
    }

    private sealed class FakeBooking : ISportyBetBookingService
    {
        public Task<BookingResult> BookGamesAsync(List<BookingSelection> selections) =>
            Task.FromResult(new BookingResult
            {
                Success = true,
                BookingCode = "LADDER1",
                BookingUrl = "https://www.sportybet.com/ng/sport/football?share=LADDER1",
                BookedCount = selections.Count,
                Message = $"Booked {selections.Count}/{selections.Count} games."
            });
    }

    private sealed class FakePricing : ISourceMarketPricingService
    {
        public IReadOnlyList<SourceMarketFixture> Fixtures { get; init; } = [];

        public Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(
            CancellationToken ct = default) =>
            Task.FromResult(Fixtures);
    }

    private sealed class FakeAdvisor : IAiAdvisorService
    {
        public Task<AiChatResponse> GetAdviceAsync(string userPrompt, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(new AiChatResponse());

        public Task<string> AnalyzeValueBetsAsync(string payload, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<BetslipDrawPickSelection>> SelectBestDrawPicksAsync(
            IReadOnlyList<BetslipDrawPickRequest> candidates,
            int count = 5,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BetslipDrawPickSelection>>([]);

        public Task<BankerPickResult> SelectBankerPicksAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default) =>
            Task.FromResult(new BankerPickResult
            {
                Picks = candidates.Take(4).Select(c => new BetslipDrawPickSelection { PredictionId = c.PredictionId }).ToList(),
                RiskNote = "ok"
            });

        public Task<LadderRankResult> RankLadderCandidatesAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderRankResult());
    }
}
