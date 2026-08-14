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

public class BetslipGenerationServicePayoutBandTests
{
    [Fact]
    public async Task GenerateDailyBetslipsAsync_Weekend_BuildsPayoutBands_NotFortyLegMegas()
    {
        if (DateTimeProvider.GetLocalDate().DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
        {
            return;
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var today = DateTimeProvider.GetLocalDate();
        var kickoff = DateTime.UtcNow.AddHours(5);
        var fixtures = new List<SourceMarketFixture>();
        var predictions = new List<Prediction>();

        for (var i = 1; i <= 80; i++)
        {
            var home = $"PayoutHome{i}";
            var away = $"PayoutAway{i}";
            var matchKickoff = kickoff.AddMinutes(i);
            var category = (i % 4) switch
            {
                0 => "BothTeamsScore",
                1 => "Over2.5Goals",
                2 => "Under2.5Goals",
                _ => "StraightWin"
            };
            var outcome = category switch
            {
                "BothTeamsScore" => "BTTS",
                "Over2.5Goals" => "Over 2.5",
                "Under2.5Goals" => "Under 2.5",
                _ => "Home"
            };

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
                FixtureKey = $"payout-fx-{i}",
                PredictionCategory = category,
                PredictedOutcome = outcome,
                ConfidenceScore = 0.90m - i * 0.001m,
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
                BttsYesOdds = 1.70,
                Over25Odds = 1.75,
                Under25Odds = 1.80,
                HomeWinOdds = 1.65,
                AwayWinOdds = 4.20,
                DrawOdds = 3.50
            });
        }

        for (var i = 81; i <= 90; i++)
        {
            var kick = kickoff.AddMinutes(i);
            predictions.Add(new Prediction
            {
                Id = i,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(kick).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = kick,
                League = "Draw League",
                HomeTeam = $"DrawHome{i}",
                AwayTeam = $"DrawAway{i}",
                FixtureKey = $"draw-fx-{i}",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ConfidenceScore = 0.70m,
                WasPublished = true,
                IsCurrentRevision = true,
                PredictionRunId = Guid.NewGuid()
            });
            fixtures.Add(new SourceMarketFixture
            {
                EventId = $"draw-evt-{i}",
                League = "Draw League",
                HomeTeam = $"DrawHome{i}",
                AwayTeam = $"DrawAway{i}",
                MatchTimeUtc = kick,
                HomeWinOdds = 2.10,
                DrawOdds = 3.40,
                AwayWinOdds = 3.60
            });
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
                BankerMinConfidence = 0.5,
                BankerMinOdds = 5,
                BankerMaxOdds = 15,
                BankerFallbackMinOdds = 4,
                BankerFallbackMaxOdds = 20,
                BankerShortlistSize = 30,
                BankerMaxPicks = 8,
                DrawSlipSize = 5,
                DrawCandidatePoolSize = 12,
                ReferenceStakeNaira = 100,
                WeekendSmallSlipCount = 2,
                WeekendMediumSlipCount = 2,
                WeekendBigSlipCount = 1,
                WeekendMegaSlipCount = 1
            }),
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.IsCurrent);

        Assert.Equal(BetslipDayKinds.Weekend, set.DayKind);
        Assert.DoesNotContain(set.Slips, s => s.TierLabel.Contains("40-50", StringComparison.Ordinal));
        Assert.DoesNotContain(set.Slips, s => s.SelectionCount >= 40);

        var ladder = set.Slips
            .Where(s => s.SlipNumber is >= 1 and <= 6)
            .OrderBy(s => s.SlipNumber)
            .ToList();

        Assert.NotEmpty(ladder);
        Assert.True(ladder.Count <= 6);
        Assert.All(ladder, s => Assert.True(s.CombinedDecimalOdds is > 1));
        Assert.Contains(set.Slips, s => s.SlipNumber == 11 || s.TierLabel.Contains("AI Draws", StringComparison.Ordinal));
    }

    private sealed class FakeBooking : ISportyBetBookingService
    {
        public Task<BookingResult> BookGamesAsync(List<BookingSelection> selections) =>
            Task.FromResult(new BookingResult
            {
                Success = true,
                BookingCode = "PAYOUT1",
                BookingUrl = "https://www.sportybet.com/ng/sport/football?share=PAYOUT1",
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
            Task.FromResult<IReadOnlyList<BetslipDrawPickSelection>>(
                candidates.Take(count).Select(c => new BetslipDrawPickSelection { PredictionId = c.PredictionId }).ToList());

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
    }
}
