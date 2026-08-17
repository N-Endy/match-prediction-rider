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

public class BetslipGenerationServiceRolloverTests
{
    [Fact]
    public async Task GenerateDailyBetslipsAsync_BooksSingleRolloverLeg_BeforeBanker_AndExcludesThatFixture()
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
        var isWeekend = today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        AddBtts(
            predictions,
            fixtures,
            ref nextId,
            today,
            kickoff,
            "RollHome",
            "RollAway",
            "roll-fx-1",
            confidence: 0.81m,
            bttsOdds: 1.30);

        for (var i = 1; i <= 8; i++)
        {
            AddBtts(
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

        for (var i = 1; i <= 20; i++)
        {
            AddBtts(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(100 + i),
                $"LadderHome{i}",
                $"LadderAway{i}",
                $"ladder-fx-{i}",
                confidence: 0.72m,
                bttsOdds: 1.60);
        }

        if (isWeekend)
        {
            AddDraw(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(3),
                "RollHome",
                "RollAway",
                "roll-fx-1",
                confidence: 0.70m,
                drawOdds: 3.50);

            for (var i = 1; i <= 8; i++)
            {
                AddDraw(
                    predictions,
                    fixtures,
                    ref nextId,
                    today,
                    kickoff.AddMinutes(400 + i),
                    $"DrawHome{i}",
                    $"DrawAway{i}",
                    $"draw-fx-{i}",
                    confidence: 0.70m,
                    drawOdds: 3.50);
            }
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var booking = new RecordingBooking();
        var service = new BetslipGenerationService(
            context,
            booking,
            new FakePricing { Fixtures = fixtures },
            new FakeAdvisor(),
            Options.Create(CreateSettings()),
            NullLogger<BetslipGenerationService>.Instance,
            Options.Create(new PredictionSettings { ValueBetMinimumEdge = 0.03 }));

        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.IsCurrent);

        var rollover = Assert.Single(set.Slips, BetslipGenerationService.IsRolloverSlip);
        Assert.Equal("Rollover", rollover.Title);
        Assert.Equal(BetslipGenerationService.RolloverTierLabel, rollover.TierLabel);
        Assert.Equal(1, rollover.SelectionCount);
        var rolloverPick = Assert.Single(rollover.Selections);
        Assert.Equal("BTTS", rolloverPick.Market);
        Assert.Equal("roll-fx-1", predictions.Single(p => p.Id == rolloverPick.PredictionId).FixtureKey);
        Assert.InRange(rolloverPick.DecimalOdds ?? 0d, 1.20, 1.50);
        Assert.InRange(rollover.CombinedDecimalOdds ?? 0d, 1.20, 1.50);
        Assert.Contains("stack-it-all", rollover.AiSummary, StringComparison.OrdinalIgnoreCase);

        var banker = Assert.Single(set.Slips, BetslipGenerationService.IsBankerSlip);
        var laterSelections = set.Slips
            .Where(s => !BetslipGenerationService.IsRolloverSlip(s))
            .SelectMany(s => s.Selections)
            .ToList();
        Assert.DoesNotContain(laterSelections, s => s.HomeTeam == "RollHome");
        Assert.DoesNotContain(banker.Selections, s => s.HomeTeam == "RollHome");

        var bookedHomes = booking.BookedHomeTeams;
        Assert.Contains("RollHome", bookedHomes);
        Assert.True(
            bookedHomes.IndexOf("RollHome") < bookedHomes.FindIndex(h => h.StartsWith("BankerHome", StringComparison.Ordinal)),
            "Rollover should be booked before Banker.");
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_OmitsRollover_WhenNoPickInOddsBand()
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
            AddBtts(
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

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var service = new BetslipGenerationService(
            context,
            new RecordingBooking(),
            new FakePricing { Fixtures = fixtures },
            new FakeAdvisor(),
            Options.Create(CreateSettings()),
            NullLogger<BetslipGenerationService>.Instance,
            Options.Create(new PredictionSettings { ValueBetMinimumEdge = 0.03 }));

        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .SingleAsync(s => s.IsCurrent);

        Assert.DoesNotContain(set.Slips, BetslipGenerationService.IsRolloverSlip);
        Assert.Contains(set.Slips, BetslipGenerationService.IsBankerSlip);
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_UsesAiRolloverId_WhenInRange()
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

        AddBtts(
            predictions,
            fixtures,
            ref nextId,
            today,
            kickoff,
            "ShortA",
            "AwayA",
            "roll-a",
            confidence: 0.85m,
            bttsOdds: 1.25);

        AddBtts(
            predictions,
            fixtures,
            ref nextId,
            today,
            kickoff.AddMinutes(5),
            "ShortB",
            "AwayB",
            "roll-b",
            confidence: 0.84m,
            bttsOdds: 1.40);

        for (var i = 1; i <= 6; i++)
        {
            AddBtts(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(20 + i),
                $"BankerHome{i}",
                $"BankerAway{i}",
                $"banker-fx-{i}",
                confidence: 0.80m,
                bttsOdds: 1.55);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var shortBId = predictions.Single(p => p.FixtureKey == "roll-b").Id;
        var advisor = new FakeAdvisor
        {
            RolloverFactory = candidates => new BankerPickResult
            {
                Picks =
                [
                    new BetslipDrawPickSelection { PredictionId = shortBId, Reason = "News supports the short." }
                ],
                RiskNote = "Verify XI."
            }
        };

        var service = new BetslipGenerationService(
            context,
            new RecordingBooking(),
            new FakePricing { Fixtures = fixtures },
            advisor,
            Options.Create(CreateSettings()),
            NullLogger<BetslipGenerationService>.Instance,
            Options.Create(new PredictionSettings { ValueBetMinimumEdge = 0.03 }));

        await service.GenerateDailyBetslipsAsync("morning");

        var rollover = await context.Betslips
            .Include(s => s.Selections)
            .SingleAsync(s => s.SlipNumber == BetslipGenerationService.RolloverSlipNumber);

        Assert.Equal(shortBId, Assert.Single(rollover.Selections).PredictionId);
        Assert.Contains("Verify XI.", rollover.AiSummary, StringComparison.Ordinal);
        Assert.NotNull(advisor.LastRolloverCandidates);
        Assert.Equal(2, advisor.LastRolloverCandidates.Count);
    }

    private static BetslipSettings CreateSettings() =>
        new()
        {
            BookingDelayMilliseconds = 0,
            MaxSlipsPerPrediction = 1,
            BankerMinOdds = 5.0,
            BankerMaxOdds = 10.0,
            BankerFallbackMinOdds = 4.0,
            BankerFallbackMaxOdds = 12.0,
            BankerMaxPicks = 8,
            LadderMinimumEdge = 0.01,
            RolloverMinOdds = 1.20,
            RolloverMaxOdds = 1.50,
            RolloverShortlistSize = 15,
            WeekendSmallSlipCount = 1,
            WeekendMediumSlipCount = 0,
            WeekendBigSlipCount = 0,
            WeekendMegaSlipCount = 0,
            SmallMinOdds = 20,
            SmallMaxOdds = 120,
            SmallFallbackMinOdds = 10,
            SmallFallbackMaxOdds = 150,
            SmallMaxPicks = 18,
            DailyMaxPicks = 18,
            DrawSlipSize = 5
        };

    private static void AddBtts(
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

    private static void AddDraw(
        List<Prediction> predictions,
        List<SourceMarketFixture> fixtures,
        ref int nextId,
        DateOnly today,
        DateTime matchKickoff,
        string home,
        string away,
        string fixtureKey,
        decimal confidence,
        double drawOdds)
    {
        var id = nextId++;
        predictions.Add(new Prediction
        {
            Id = id,
            Date = today.ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
            MatchLocalDate = today,
            MatchDateTime = matchKickoff,
            League = "Draw League",
            HomeTeam = home,
            AwayTeam = away,
            FixtureKey = fixtureKey,
            PredictionCategory = "Draw",
            PredictedOutcome = "Draw",
            ConfidenceScore = confidence,
            WasPublished = true,
            IsCurrentRevision = true,
            PredictionRunId = Guid.NewGuid()
        });
        fixtures.Add(new SourceMarketFixture
        {
            EventId = $"draw-evt-{id}",
            League = "Draw League",
            HomeTeam = home,
            AwayTeam = away,
            MatchTimeUtc = matchKickoff,
            HomeWinOdds = 2.20,
            DrawOdds = drawOdds,
            AwayWinOdds = 3.40
        });
    }

    private sealed class RecordingBooking : ISportyBetBookingService
    {
        public List<string> BookedHomeTeams { get; } = [];

        public Task<BookingResult> BookGamesAsync(List<BookingSelection> selections)
        {
            BookedHomeTeams.AddRange(selections.Select(s => s.HomeTeam));
            return Task.FromResult(new BookingResult
            {
                Success = true,
                BookingCode = "ROLL1",
                BookingUrl = "https://www.sportybet.com/ng/sport/football?share=ROLL1",
                BookedCount = selections.Count,
                Message = $"Booked {selections.Count}/{selections.Count} games."
            });
        }
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
        public Func<IReadOnlyList<BankerPickRequest>, BankerPickResult> RolloverFactory { get; init; } =
            _ => new BankerPickResult();

        public IReadOnlyList<BankerPickRequest>? LastRolloverCandidates { get; private set; }

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

        public Task<BankerPickResult> SelectRolloverPickAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default)
        {
            LastRolloverCandidates = candidates.ToList();
            return Task.FromResult(RolloverFactory(candidates));
        }

        public Task<LadderRankResult> RankLadderCandidatesAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderRankResult());

        public Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
            IReadOnlyList<BetslipScreenRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new BetslipScreenResult
            {
                Scores = candidates.Select(c => new BetslipScreenPick
                {
                    PredictionId = c.PredictionId,
                    Score = (double)c.Confidence * 100d
                }).ToList()
            });

        public Task<LadderComposeResult> ComposeLadderSlipsAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            IReadOnlyList<LadderComposeBandRequest> bands,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderComposeResult());
    }
}
