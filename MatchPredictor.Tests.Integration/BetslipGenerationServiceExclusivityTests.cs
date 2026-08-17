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

public class BetslipGenerationServiceExclusivityTests
{
    [Fact]
    public async Task GenerateDailyBetslipsAsync_BankerFixturesAbsentFromLadder()
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

        for (var i = 1; i <= 8; i++)
        {
            AddMainPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"BankerHome{i}",
                $"BankerAway{i}",
                $"shared-banker-fx-{i}",
                confidence: 0.92m - i * 0.005m,
                category: "BothTeamsScore",
                bttsOdds: 1.55);
        }

        for (var i = 1; i <= 60; i++)
        {
            AddMainPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(100 + i),
                $"LadderHome{i}",
                $"LadderAway{i}",
                $"ladder-only-fx-{i}",
                confidence: 0.85m - i * 0.001m,
                category: i % 2 == 0 ? "Over2.5Goals" : "Under2.5Goals",
                overOdds: 1.70,
                underOdds: 1.75);
        }

        for (var i = 1; i <= 5; i++)
        {
            AddMainPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(400 + i),
                $"NoEdgeHome{i}",
                $"NoEdgeAway{i}",
                $"no-edge-fx-{i}",
                confidence: 0.52m,
                category: "BothTeamsScore",
                bttsOdds: 1.55);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var fixtureByPredictionId = predictions.ToDictionary(p => p.Id, p => p.FixtureKey);
        var service = CreateService(context, fixtures);
        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.IsCurrent);

        var banker = set.Slips.SingleOrDefault(s => s.SlipNumber == BetslipGenerationService.BankerSlipNumber);
        Assert.NotNull(banker);
        Assert.NotEmpty(banker.Selections);

        var ladder = set.Slips.Where(BetslipGenerationService.IsLadderSlip).ToList();
        Assert.NotEmpty(ladder);

        var bankerFixtures = SelectionFixtureKeys(banker.Selections, fixtureByPredictionId);
        var ladderFixtures = SelectionFixtureKeys(ladder.SelectMany(s => s.Selections), fixtureByPredictionId);

        Assert.Empty(bankerFixtures.Intersect(ladderFixtures, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_NoFixtureSharedAcrossPersistedSlips()
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

        for (var i = 1; i <= 70; i++)
        {
            AddMainPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"ExHome{i}",
                $"ExAway{i}",
                $"ex-fx-{i}",
                confidence: 0.90m - i * 0.001m,
                category: (i % 4) switch
                {
                    0 => "BothTeamsScore",
                    1 => "Over2.5Goals",
                    2 => "Under2.5Goals",
                    _ => "StraightWin"
                },
                bttsOdds: 1.68,
                overOdds: 1.72,
                underOdds: 1.78,
                homeOdds: 1.65);
        }

        if (isWeekend)
        {
            for (var i = 1; i <= 10; i++)
            {
                var matchKickoff = kickoff.AddMinutes(200 + i);
                predictions.Add(new Prediction
                {
                    Id = nextId++,
                    Date = today.ToString("dd-MM-yyyy"),
                    Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
                    MatchLocalDate = today,
                    MatchDateTime = matchKickoff,
                    League = "Draw League",
                    HomeTeam = $"UniqueDrawHome{i}",
                    AwayTeam = $"UniqueDrawAway{i}",
                    FixtureKey = $"unique-draw-fx-{i}",
                    PredictionCategory = "Draw",
                    PredictedOutcome = "Draw",
                    ConfidenceScore = 0.70m,
                    WasPublished = true,
                    IsCurrentRevision = true,
                    PredictionRunId = Guid.NewGuid()
                });
                fixtures.Add(new SourceMarketFixture
                {
                    EventId = $"unique-draw-evt-{i}",
                    League = "Draw League",
                    HomeTeam = $"UniqueDrawHome{i}",
                    AwayTeam = $"UniqueDrawAway{i}",
                    MatchTimeUtc = matchKickoff,
                    HomeWinOdds = 2.20,
                    DrawOdds = 3.50,
                    AwayWinOdds = 3.40
                });
            }
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var fixtureByPredictionId = predictions.ToDictionary(p => p.Id, p => p.FixtureKey);
        var service = CreateService(context, fixtures);
        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.IsCurrent);

        Assert.NotEmpty(set.Slips);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slip in set.Slips)
        {
            foreach (var fixtureKey in SelectionFixtureKeys(slip.Selections, fixtureByPredictionId))
            {
                Assert.True(
                    seen.Add(fixtureKey),
                    $"Fixture '{fixtureKey}' appeared on more than one slip.");
            }
        }

        if (isWeekend)
        {
            var draws = set.Slips.SingleOrDefault(s =>
                string.Equals(s.TierLabel, "AI Draws (5)", StringComparison.Ordinal));
            if (draws is not null)
            {
                var drawFixtures = SelectionFixtureKeys(draws.Selections, fixtureByPredictionId);
                Assert.DoesNotContain(drawFixtures, k =>
                    k.StartsWith("ex-fx-", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_Weekend_ExcludesUsedFixturesFromDrawPool()
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
        var nextId = 1;
        var advisor = new RecordingAdvisor();

        for (var i = 1; i <= 50; i++)
        {
            AddMainPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"WkHome{i}",
                $"WkAway{i}",
                $"wk-fx-{i}",
                confidence: 0.88m - i * 0.001m,
                category: "BothTeamsScore",
                bttsOdds: 1.60);
        }

        for (var i = 1; i <= 8; i++)
        {
            predictions.Add(new Prediction
            {
                Id = nextId++,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(kickoff.AddMinutes(i)).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = kickoff.AddMinutes(i),
                League = "Test League",
                HomeTeam = $"WkHome{i}",
                AwayTeam = $"WkAway{i}",
                FixtureKey = $"wk-fx-{i}",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ConfidenceScore = 0.99m,
                WasPublished = true,
                IsCurrentRevision = true,
                PredictionRunId = Guid.NewGuid()
            });
        }

        for (var i = 1; i <= 8; i++)
        {
            predictions.Add(new Prediction
            {
                Id = nextId++,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(kickoff.AddMinutes(300 + i)).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = kickoff.AddMinutes(300 + i),
                League = "Draw League",
                HomeTeam = $"FreeDrawHome{i}",
                AwayTeam = $"FreeDrawAway{i}",
                FixtureKey = $"free-draw-fx-{i}",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ConfidenceScore = 0.65m,
                WasPublished = true,
                IsCurrentRevision = true,
                PredictionRunId = Guid.NewGuid()
            });
            fixtures.Add(new SourceMarketFixture
            {
                EventId = $"draw-evt-{i}",
                League = "Draw League",
                HomeTeam = $"FreeDrawHome{i}",
                AwayTeam = $"FreeDrawAway{i}",
                MatchTimeUtc = kickoff.AddMinutes(300 + i),
                HomeWinOdds = 2.20,
                DrawOdds = 3.50,
                AwayWinOdds = 3.40
            });
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var fixtureByPredictionId = predictions.ToDictionary(p => p.Id, p => p.FixtureKey);
        var service = CreateService(context, fixtures, advisor);
        await service.GenerateDailyBetslipsAsync("morning");

        Assert.NotNull(advisor.LastDrawCandidates);
        Assert.DoesNotContain(
            advisor.LastDrawCandidates,
            c => c.HomeTeam.StartsWith("WkHome", StringComparison.Ordinal));
        Assert.All(
            advisor.LastDrawCandidates,
            c => Assert.StartsWith("FreeDrawHome", c.HomeTeam, StringComparison.Ordinal));

        var draws = await context.Betslips
            .Include(s => s.Selections)
            .SingleAsync(s => s.TierLabel == "AI Draws (5)");

        var drawFixtures = SelectionFixtureKeys(draws.Selections, fixtureByPredictionId);
        Assert.All(drawFixtures, key =>
            Assert.StartsWith("free-draw-fx-", key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_Weekend_SendsDrawPoolBeyondOldTop12()
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
        var nextId = 1;
        var advisor = new LastFiveDrawAdvisor();

        for (var i = 1; i <= 8; i++)
        {
            AddMainPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"BankerHome{i}",
                $"BankerAway{i}",
                $"banker-fx-{i}",
                confidence: 0.92m - i * 0.005m,
                category: "BothTeamsScore",
                bttsOdds: 1.55);
        }

        for (var i = 1; i <= 15; i++)
        {
            var id = nextId++;
            var matchKickoff = kickoff.AddMinutes(300 + i);
            predictions.Add(new Prediction
            {
                Id = id,
                Date = today.ToString("dd-MM-yyyy"),
                Time = DateTimeProvider.ConvertUtcToLocal(matchKickoff).ToString("HH:mm"),
                MatchLocalDate = today,
                MatchDateTime = matchKickoff,
                League = "Draw League",
                HomeTeam = $"FreeDrawHome{i}",
                AwayTeam = $"FreeDrawAway{i}",
                FixtureKey = $"free-draw-fx-{i}",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ConfidenceScore = 0.80m - i * 0.01m,
                WasPublished = true,
                IsCurrentRevision = true,
                PredictionRunId = Guid.NewGuid()
            });
            fixtures.Add(new SourceMarketFixture
            {
                EventId = $"draw-evt-{i}",
                League = "Draw League",
                HomeTeam = $"FreeDrawHome{i}",
                AwayTeam = $"FreeDrawAway{i}",
                MatchTimeUtc = matchKickoff,
                HomeWinOdds = 2.20,
                DrawOdds = 3.50,
                AwayWinOdds = 3.40
            });
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var service = CreateService(context, fixtures, advisor);
        await service.GenerateDailyBetslipsAsync("morning");

        Assert.NotNull(advisor.LastDrawCandidates);
        Assert.Equal(15, advisor.LastDrawCandidates.Count);

        var draws = await context.Betslips
            .Include(s => s.Selections)
            .SingleAsync(s => s.TierLabel == "AI Draws (5)");

        Assert.Equal(5, draws.Selections.Count);
        Assert.Contains(draws.Selections, s => s.PredictionId == 21);
        Assert.Contains(draws.Selections, s => s.PredictionId == 22);
        Assert.Contains(draws.Selections, s => s.PredictionId == 23);
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_RanksLadderPoolAfterBankerExclusion()
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
        var advisor = new RecordingAdvisor();

        for (var i = 1; i <= 8; i++)
        {
            AddMainPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"BankerHome{i}",
                $"BankerAway{i}",
                $"shared-banker-fx-{i}",
                confidence: 0.92m - i * 0.005m,
                category: "BothTeamsScore",
                bttsOdds: 1.55);
        }

        for (var i = 1; i <= 40; i++)
        {
            AddMainPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(100 + i),
                $"LadderHome{i}",
                $"LadderAway{i}",
                $"ladder-only-fx-{i}",
                confidence: 0.85m - i * 0.001m,
                category: i % 2 == 0 ? "Over2.5Goals" : "Under2.5Goals",
                overOdds: 1.70,
                underOdds: 1.75);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var service = CreateService(context, fixtures, advisor);
        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.IsCurrent);

        var banker = set.Slips.Single(s => s.SlipNumber == BetslipGenerationService.BankerSlipNumber);
        var bankerHomes = banker.Selections
            .Select(s => s.HomeTeam)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotNull(advisor.LastLadderCandidates);
        Assert.NotEmpty(advisor.LastLadderCandidates);
        Assert.DoesNotContain(
            advisor.LastLadderCandidates,
            c => bankerHomes.Contains(c.HomeTeam));
        Assert.All(
            advisor.LastLadderCandidates,
            c => Assert.True(
                c.HomeTeam.StartsWith("LadderHome", StringComparison.Ordinal) ||
                c.HomeTeam.StartsWith("BankerHome", StringComparison.Ordinal)));

        var ladder = set.Slips.Where(BetslipGenerationService.IsLadderSlip).ToList();
        Assert.NotEmpty(ladder);

        var fixtureByPredictionId = predictions.ToDictionary(p => p.Id, p => p.FixtureKey);
        var bankerFixtures = SelectionFixtureKeys(banker.Selections, fixtureByPredictionId);
        var ladderFixtures = SelectionFixtureKeys(ladder.SelectMany(s => s.Selections), fixtureByPredictionId);
        Assert.Empty(bankerFixtures.Intersect(ladderFixtures, StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> SelectionFixtureKeys(
        IEnumerable<BetslipSelection> selections,
        IReadOnlyDictionary<int, string> fixtureByPredictionId)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            if (selection.PredictionId is int predictionId &&
                fixtureByPredictionId.TryGetValue(predictionId, out var key) &&
                !string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static BetslipGenerationService CreateService(
        ApplicationDbContext context,
        IReadOnlyList<SourceMarketFixture> fixtures,
        IAiAdvisorService? advisor = null) =>
        new(
            context,
            new FakeBooking(),
            new FakePricing { Fixtures = fixtures },
            advisor ?? new FakeAdvisor(),
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                MaxSlipsPerPrediction = 1,
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

    private static void AddMainPrediction(
        List<Prediction> predictions,
        List<SourceMarketFixture> fixtures,
        ref int nextId,
        DateOnly today,
        DateTime matchKickoff,
        string home,
        string away,
        string fixtureKey,
        decimal confidence,
        string category,
        double bttsOdds = 2.0,
        double overOdds = 2.0,
        double underOdds = 2.0,
        double homeOdds = 2.0)
    {
        var id = nextId++;
        var outcome = category switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" => "Over 2.5",
            "Under2.5Goals" => "Under 2.5",
            _ => "Home"
        };

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
            PredictionCategory = category,
            PredictedOutcome = outcome,
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
            BttsYesOdds = bttsOdds,
            Over25Odds = overOdds,
            Under25Odds = underOdds,
            HomeWinOdds = homeOdds,
            AwayWinOdds = 4.20,
            DrawOdds = 3.50
        });
    }

    private sealed class FakeBooking : ISportyBetBookingService
    {
        public Task<BookingResult> BookGamesAsync(List<BookingSelection> selections) =>
            Task.FromResult(new BookingResult
            {
                Success = true,
                BookingCode = "EXCL1",
                BookingUrl = "https://www.sportybet.com/ng/sport/football?share=EXCL1",
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

    private class FakeAdvisor : IAiAdvisorService
    {
        public Task<AiChatResponse> GetAdviceAsync(string userPrompt, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(new AiChatResponse());

        public Task<string> AnalyzeValueBetsAsync(string payload, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public virtual Task<IReadOnlyList<BetslipDrawPickSelection>> SelectBestDrawPicksAsync(
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

        public virtual Task<LadderRankResult> RankLadderCandidatesAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderRankResult());

        public virtual Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
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

        public virtual Task<LadderComposeResult> ComposeLadderSlipsAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            IReadOnlyList<LadderComposeBandRequest> bands,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderComposeResult());
    }

    private sealed class RecordingAdvisor : FakeAdvisor
    {
        public IReadOnlyList<BetslipDrawPickRequest>? LastDrawCandidates { get; private set; }
        public IReadOnlyList<LadderRankRequest>? LastLadderCandidates { get; private set; }
        public List<IReadOnlyList<BetslipScreenRequest>> ScreenedBatches { get; } = [];

        public override Task<IReadOnlyList<BetslipDrawPickSelection>> SelectBestDrawPicksAsync(
            IReadOnlyList<BetslipDrawPickRequest> candidates,
            int count = 5,
            CancellationToken ct = default)
        {
            LastDrawCandidates = candidates.ToList();
            return base.SelectBestDrawPicksAsync(candidates, count, ct);
        }

        public override Task<LadderRankResult> RankLadderCandidatesAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            CancellationToken ct = default)
        {
            LastLadderCandidates = candidates.ToList();
            return base.RankLadderCandidatesAsync(candidates, ct);
        }

        public override Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
            IReadOnlyList<BetslipScreenRequest> candidates,
            CancellationToken ct = default)
        {
            ScreenedBatches.Add(candidates.ToList());
            return base.ScreenBetslipCandidatesAsync(candidates, ct);
        }

        public override Task<LadderComposeResult> ComposeLadderSlipsAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            IReadOnlyList<LadderComposeBandRequest> bands,
            CancellationToken ct = default)
        {
            LastLadderCandidates = candidates.ToList();
            return base.ComposeLadderSlipsAsync(candidates, bands, ct);
        }
    }

    private sealed class LastFiveDrawAdvisor : FakeAdvisor
    {
        public IReadOnlyList<BetslipDrawPickRequest>? LastDrawCandidates { get; private set; }

        public override Task<IReadOnlyList<BetslipDrawPickSelection>> SelectBestDrawPicksAsync(
            IReadOnlyList<BetslipDrawPickRequest> candidates,
            int count = 5,
            CancellationToken ct = default)
        {
            LastDrawCandidates = candidates.ToList();
            return Task.FromResult<IReadOnlyList<BetslipDrawPickSelection>>(
                candidates.TakeLast(count).Select(c => new BetslipDrawPickSelection { PredictionId = c.PredictionId }).ToList());
        }
    }
}
