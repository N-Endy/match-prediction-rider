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

public class BetslipGenerationServiceRunExclusivityTests
{
    [Fact]
    public async Task GenerateDailyBetslipsAsync_Midday_UsesFreshRolloverAndBanker_WhenLeftoverLegsExist()
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

        AddBtts(predictions, fixtures, ref nextId, today, kickoff, "RollHome1", "RollAway1", "roll-fx-1", 0.81m, 1.30);
        AddBtts(predictions, fixtures, ref nextId, today, kickoff.AddMinutes(1), "RollHome2", "RollAway2", "roll-fx-2", 0.80m, 1.35);

        for (var i = 1; i <= 8; i++)
        {
            AddBtts(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(10 + i),
                $"BankerHome{i}",
                $"BankerAway{i}",
                $"banker-fx-{i}",
                0.92m - i * 0.005m,
                1.55);
        }

        for (var i = 1; i <= 8; i++)
        {
            AddBtts(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(30 + i),
                $"FreshBankerHome{i}",
                $"FreshBankerAway{i}",
                $"fresh-banker-fx-{i}",
                0.80m - i * 0.001m,
                1.55);
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
                0.72m,
                1.60);
        }

        if (isWeekend)
        {
            AddWeekendDraws(predictions, fixtures, ref nextId, today, kickoff);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var fixtureByPredictionId = predictions.ToDictionary(p => p.Id, p => p.FixtureKey);
        var service = CreateService(context, fixtures);

        await service.GenerateDailyBetslipsAsync("morning");
        await service.GenerateDailyBetslipsAsync("midday");

        var morning = await LoadSetAsync(context, BetslipRunLabels.Morning);
        var midday = await LoadSetAsync(context, BetslipRunLabels.Midday);

        var morningRollover = FixtureKeys(morning, BetslipGenerationService.IsRolloverSlip, fixtureByPredictionId);
        var middayRollover = FixtureKeys(midday, BetslipGenerationService.IsRolloverSlip, fixtureByPredictionId);
        Assert.Equal("roll-fx-1", Assert.Single(morningRollover));
        Assert.Equal("roll-fx-2", Assert.Single(middayRollover));

        var morningBanker = FixtureKeys(morning, BetslipGenerationService.IsBankerSlip, fixtureByPredictionId);
        var middayBanker = FixtureKeys(midday, BetslipGenerationService.IsBankerSlip, fixtureByPredictionId);
        Assert.NotEmpty(morningBanker);
        Assert.NotEmpty(middayBanker);
        Assert.Empty(morningBanker.Intersect(middayBanker, StringComparer.OrdinalIgnoreCase));

        var middayLadder = FixtureKeys(midday, BetslipGenerationService.IsLadderSlip, fixtureByPredictionId);
        Assert.DoesNotContain("roll-fx-1", middayLadder);
        Assert.DoesNotContain("roll-fx-2", middayLadder);
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_Midday_OmitsRolloverAndMayReuseBanker_WhenNoFreshCombo()
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

        AddBtts(predictions, fixtures, ref nextId, today, kickoff, "RollHome", "RollAway", "roll-fx-1", 0.81m, 1.30);

        for (var i = 1; i <= 4; i++)
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
                0.80m,
                1.55);
        }

        if (isWeekend)
        {
            AddWeekendDraws(predictions, fixtures, ref nextId, today, kickoff);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var fixtureByPredictionId = predictions.ToDictionary(p => p.Id, p => p.FixtureKey);
        var service = CreateService(context, fixtures);

        await service.GenerateDailyBetslipsAsync("morning");
        await service.GenerateDailyBetslipsAsync("midday");

        var morning = await LoadSetAsync(context, BetslipRunLabels.Morning);
        var midday = await LoadSetAsync(context, BetslipRunLabels.Midday);

        Assert.Contains(morning.Slips, BetslipGenerationService.IsRolloverSlip);
        Assert.DoesNotContain(midday.Slips, BetslipGenerationService.IsRolloverSlip);

        var morningBanker = FixtureKeys(morning, BetslipGenerationService.IsBankerSlip, fixtureByPredictionId);
        var middayBanker = FixtureKeys(midday, BetslipGenerationService.IsBankerSlip, fixtureByPredictionId);
        Assert.NotEmpty(morningBanker);
        Assert.True(morningBanker.SetEquals(middayBanker));

        var middayLater = FixtureKeys(
            midday,
            s => !BetslipGenerationService.IsRolloverSlip(s),
            fixtureByPredictionId);
        Assert.DoesNotContain("roll-fx-1", middayLater);
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_Midday_Unchanged_WhenNoMorningSet()
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

        AddBtts(predictions, fixtures, ref nextId, today, kickoff, "RollHome", "RollAway", "roll-fx-1", 0.81m, 1.30);

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
                0.80m,
                1.55);
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
                0.72m,
                1.60);
        }

        if (isWeekend)
        {
            AddWeekendDraws(predictions, fixtures, ref nextId, today, kickoff);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var fixtureByPredictionId = predictions.ToDictionary(p => p.Id, p => p.FixtureKey);
        var service = CreateService(context, fixtures);
        await service.GenerateDailyBetslipsAsync("midday");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync();

        Assert.Equal(BetslipRunLabels.Midday, set.RunLabel);
        var rollover = Assert.Single(set.Slips, BetslipGenerationService.IsRolloverSlip);
        Assert.Equal("roll-fx-1", fixtureByPredictionId[rollover.Selections.Single().PredictionId!.Value]);
        Assert.Contains(set.Slips, BetslipGenerationService.IsBankerSlip);
    }

    private static async Task<BetslipSet> LoadSetAsync(ApplicationDbContext context, string runLabel) =>
        await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.RunLabel == runLabel);

    private static HashSet<string> FixtureKeys(
        BetslipSet set,
        Func<Betslip, bool> slipPredicate,
        IReadOnlyDictionary<int, string> fixtureByPredictionId)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slip in set.Slips.Where(slipPredicate))
        {
            foreach (var selection in slip.Selections)
            {
                if (selection.PredictionId is int predictionId &&
                    fixtureByPredictionId.TryGetValue(predictionId, out var key) &&
                    !string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    private static BetslipGenerationService CreateService(
        ApplicationDbContext context,
        IReadOnlyList<SourceMarketFixture> fixtures) =>
        new(
            context,
            new FakeBooking(),
            new FakePricing { Fixtures = fixtures },
            new FakeAdvisor(),
            Options.Create(CreateSettings()),
            NullLogger<BetslipGenerationService>.Instance,
            Options.Create(new PredictionSettings { ValueBetMinimumEdge = 0.03 }));

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

    private static void AddWeekendDraws(
        List<Prediction> predictions,
        List<SourceMarketFixture> fixtures,
        ref int nextId,
        DateOnly today,
        DateTime kickoff)
    {
        for (var i = 1; i <= 8; i++)
        {
            var matchKickoff = kickoff.AddMinutes(400 + i);
            var id = nextId++;
            predictions.Add(new Prediction
            {
                Id = id,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = matchKickoff,
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
                EventId = $"draw-evt-{id}",
                League = "Draw League",
                HomeTeam = $"DrawHome{i}",
                AwayTeam = $"DrawAway{i}",
                MatchTimeUtc = matchKickoff,
                HomeWinOdds = 2.20,
                DrawOdds = 3.50,
                AwayWinOdds = 3.40
            });
        }
    }

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

    private sealed class FakeBooking : ISportyBetBookingService
    {
        public Task<BookingResult> BookGamesAsync(List<BookingSelection> selections) =>
            Task.FromResult(new BookingResult
            {
                Success = true,
                BookingCode = "RUNEX1",
                BookingUrl = "https://www.sportybet.com/ng/sport/football?share=RUNEX1",
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

        public Task<BankerPickResult> SelectRolloverPickAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default) =>
            Task.FromResult(new BankerPickResult());

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
