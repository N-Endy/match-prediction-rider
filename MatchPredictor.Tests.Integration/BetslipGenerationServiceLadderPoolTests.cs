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
        var ladder = set.Slips.Where(BetslipGenerationService.IsLadderSlip).ToList();
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

    [Fact]
    public async Task GenerateDailyBetslipsAsync_PacksLadderFromOnePercentLeftovers_WhenScreenRejectsThem()
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
            new SparseScreenAdvisor(),
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                MaxSlipsPerPrediction = 1,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0,
                BankerMaxPicks = 8,
                LadderMinimumEdge = 0.01,
                WeekendSmallSlipCount = 1,
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

        Assert.Contains(set.Slips, BetslipGenerationService.IsBankerSlip);
        var ladder = set.Slips.Where(BetslipGenerationService.IsLadderSlip).ToList();
        Assert.NotEmpty(ladder);

        var thinIds = predictions
            .Where(p => p.FixtureKey.StartsWith("thin-edge-fx-", StringComparison.Ordinal))
            .Select(p => p.Id)
            .ToHashSet();
        var ladderIds = ladder
            .SelectMany(s => s.Selections)
            .Select(s => s.PredictionId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        Assert.NotEmpty(ladderIds.Intersect(thinIds));
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_ScreensLiveQuotedCardInBatches()
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

        for (var i = 1; i <= 30; i++)
        {
            AddBttsPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"BatchHome{i}",
                $"BatchAway{i}",
                $"batch-fx-{i}",
                confidence: 0.80m,
                bttsOdds: 1.55);
        }

        var unmatchedKickoff = kickoff.AddMinutes(400);
        predictions.Add(new Prediction
        {
            Id = nextId++,
            Date = today.ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.ConvertUtcToLocal(unmatchedKickoff).ToString("HH:mm"),
            MatchLocalDate = today,
            MatchDateTime = unmatchedKickoff,
            League = "Test League",
            HomeTeam = "UnmatchedHome",
            AwayTeam = "UnmatchedAway",
            FixtureKey = "unmatched-fx",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ConfidenceScore = 0.99m,
            WasPublished = true,
            IsCurrentRevision = true,
            PredictionRunId = Guid.NewGuid()
        });

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var advisor = new RecordingScreenAdvisor();
        var service = new BetslipGenerationService(
            context,
            new FakeBooking(),
            new FakePricing { Fixtures = fixtures },
            advisor,
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                ScreenBatchSize = 10,
                BankerMinConfidence = 0.60,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0,
                BankerMaxPicks = 8,
                WeekendSmallSlipCount = 1,
                WeekendMediumSlipCount = 0,
                WeekendBigSlipCount = 0,
                WeekendMegaSlipCount = 0
            }),
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("morning");

        Assert.Equal(3, advisor.ScreenedBatches.Count);
        Assert.All(advisor.ScreenedBatches, batch => Assert.Equal(10, batch.Count));
        var screenedIds = advisor.ScreenedBatches.SelectMany(b => b.Select(c => c.PredictionId)).ToHashSet();
        Assert.Equal(30, screenedIds.Count);
        Assert.DoesNotContain(advisor.ScreenedBatches.SelectMany(b => b), c => c.HomeTeam == "UnmatchedHome");
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_DropsUnknownScreenIds_AndKeepsFailedBatchAsPassers()
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

        for (var i = 1; i <= 12; i++)
        {
            AddBttsPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"MixHome{i}",
                $"MixAway{i}",
                $"mix-fx-{i}",
                confidence: 0.80m,
                bttsOdds: 1.55);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var advisor = new MixedScreenAdvisor();
        var service = new BetslipGenerationService(
            context,
            new FakeBooking(),
            new FakePricing { Fixtures = fixtures },
            advisor,
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                ScreenBatchSize = 6,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0,
                BankerMaxPicks = 8
            }),
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("morning");

        Assert.NotNull(advisor.LastBankerCandidates);
        Assert.Contains(advisor.LastBankerCandidates, c => c.PredictionId <= 6);
        Assert.Contains(advisor.LastBankerCandidates, c => c.PredictionId > 6);
        Assert.DoesNotContain(advisor.LastBankerCandidates, c => c.PredictionId == 999);
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_PacksLadderBand_WhenAiComposeMissesOdds()
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
            AddBttsPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(i),
                $"PackHome{i}",
                $"PackAway{i}",
                $"pack-fx-{i}",
                confidence: 0.80m,
                bttsOdds: 1.55);
        }

        for (var i = 1; i <= 20; i++)
        {
            AddBttsPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(100 + i),
                $"LadderHome{i}",
                $"LadderAway{i}",
                $"pack-ladder-fx-{i}",
                confidence: 0.72m,
                bttsOdds: 1.60);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var service = new BetslipGenerationService(
            context,
            new FakeBooking(),
            new FakePricing { Fixtures = fixtures },
            new WeakLadderComposeAdvisor(),
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0,
                BankerMaxPicks = 8,
                WeekendSmallSlipCount = 1,
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
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("morning");

        var set = await context.BetslipSets
            .Include(s => s.Slips)
            .ThenInclude(s => s.Selections)
            .SingleAsync(s => s.IsCurrent);

        var ladder = set.Slips.Where(BetslipGenerationService.IsLadderSlip).ToList();
        Assert.NotEmpty(ladder);
        Assert.Contains(ladder, s => s.Selections.Count > 1);
    }

    [Fact]
    public async Task GenerateDailyBetslipsAsync_CollapsesScreenedPassersToOnePerFixture()
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
        var dualKickoff = kickoff.AddMinutes(1);

        var bttsId = nextId++;
        predictions.Add(new Prediction
        {
            Id = bttsId,
            Date = today.ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.ConvertUtcToLocal(dualKickoff).ToString("HH:mm"),
            MatchLocalDate = today,
            MatchDateTime = dualKickoff,
            League = "Test League",
            HomeTeam = "DualHome",
            AwayTeam = "DualAway",
            FixtureKey = "dual-fx",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ConfidenceScore = 0.90m,
            WasPublished = true,
            IsCurrentRevision = true,
            PredictionRunId = Guid.NewGuid()
        });

        var overId = nextId++;
        predictions.Add(new Prediction
        {
            Id = overId,
            Date = today.ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.ConvertUtcToLocal(dualKickoff).ToString("HH:mm"),
            MatchLocalDate = today,
            MatchDateTime = dualKickoff,
            League = "Test League",
            HomeTeam = "DualHome",
            AwayTeam = "DualAway",
            FixtureKey = "dual-fx",
            PredictionCategory = "Over2.5Goals",
            PredictedOutcome = "Over 2.5",
            ConfidenceScore = 0.70m,
            WasPublished = true,
            IsCurrentRevision = true,
            PredictionRunId = Guid.NewGuid()
        });

        fixtures.Add(new SourceMarketFixture
        {
            EventId = "evt-dual",
            League = "Test League",
            HomeTeam = "DualHome",
            AwayTeam = "DualAway",
            MatchTimeUtc = dualKickoff,
            BttsYesOdds = 1.55,
            Over25Odds = 1.70
        });

        for (var i = 1; i <= 6; i++)
        {
            AddBttsPrediction(
                predictions,
                fixtures,
                ref nextId,
                today,
                kickoff.AddMinutes(20 + i),
                $"OtherHome{i}",
                $"OtherAway{i}",
                $"other-fx-{i}",
                confidence: 0.80m,
                bttsOdds: 1.55);
        }

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();

        var advisor = new CollapseScoreAdvisor();
        var service = new BetslipGenerationService(
            context,
            new FakeBooking(),
            new FakePricing { Fixtures = fixtures },
            advisor,
            Options.Create(new BetslipSettings
            {
                BookingDelayMilliseconds = 0,
                BankerMinOdds = 5.0,
                BankerMaxOdds = 10.0,
                BankerFallbackMinOdds = 4.0,
                BankerFallbackMaxOdds = 12.0,
                BankerMaxPicks = 8
            }),
            NullLogger<BetslipGenerationService>.Instance);

        await service.GenerateDailyBetslipsAsync("morning");

        Assert.NotNull(advisor.LastBankerCandidates);
        Assert.Contains(advisor.LastBankerCandidates, c => c.PredictionId == overId);
        Assert.DoesNotContain(advisor.LastBankerCandidates, c => c.PredictionId == bttsId);
        Assert.Equal(1, advisor.LastBankerCandidates.Count(c => c.HomeTeam == "DualHome"));
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

    private class FakeAdvisor : IAiAdvisorService
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

        public Task<LadderRankResult> RankLadderCandidatesAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderRankResult());

        public virtual Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
            IReadOnlyList<BetslipScreenRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new BetslipScreenResult
            {
                Passed = candidates.Select(c => new BetslipScreenPick
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

        public virtual Task<BankerPickResult> SelectBankerPicksAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default) =>
            Task.FromResult(new BankerPickResult
            {
                Picks = candidates.Take(4).Select(c => new BetslipDrawPickSelection { PredictionId = c.PredictionId }).ToList(),
                RiskNote = "ok"
            });

        public virtual Task<BankerPickResult> SelectRolloverPickAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default) =>
            Task.FromResult(new BankerPickResult());
    }

    private sealed class SparseScreenAdvisor : FakeAdvisor
    {
        public override Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
            IReadOnlyList<BetslipScreenRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new BetslipScreenResult
            {
                Passed = candidates
                    .Where(c => c.Confidence >= 0.75m)
                    .Select(c => new BetslipScreenPick
                    {
                        PredictionId = c.PredictionId,
                        Score = 90
                    })
                    .ToList()
            });
    }

    private sealed class RecordingScreenAdvisor : FakeAdvisor
    {
        public List<IReadOnlyList<BetslipScreenRequest>> ScreenedBatches { get; } = [];

        public override Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
            IReadOnlyList<BetslipScreenRequest> candidates,
            CancellationToken ct = default)
        {
            ScreenedBatches.Add(candidates.ToList());
            return base.ScreenBetslipCandidatesAsync(candidates, ct);
        }
    }

    private sealed class MixedScreenAdvisor : FakeAdvisor
    {
        private int _batch;
        public IReadOnlyList<BankerPickRequest>? LastBankerCandidates { get; private set; }

        public override Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
            IReadOnlyList<BetslipScreenRequest> candidates,
            CancellationToken ct = default)
        {
            _batch++;
            if (_batch == 2)
            {
                throw new InvalidOperationException("screening timeout");
            }

            return Task.FromResult(new BetslipScreenResult
            {
                Passed = candidates.Select(c => new BetslipScreenPick
                {
                    PredictionId = c.PredictionId,
                    Score = 80
                }).Append(new BetslipScreenPick { PredictionId = 999, Score = 99, Reason = "invented" }).ToList()
            });
        }

        public override Task<BankerPickResult> SelectBankerPicksAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default)
        {
            LastBankerCandidates = candidates.ToList();
            return base.SelectBankerPicksAsync(candidates, minOdds, maxOdds, ct);
        }
    }

    private sealed class WeakLadderComposeAdvisor : FakeAdvisor
    {
        public override Task<LadderComposeResult> ComposeLadderSlipsAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            IReadOnlyList<LadderComposeBandRequest> bands,
            CancellationToken ct = default)
        {
            var first = candidates.FirstOrDefault();
            if (first is null || bands.Count == 0)
            {
                return Task.FromResult(new LadderComposeResult());
            }

            return Task.FromResult(new LadderComposeResult
            {
                Slips =
                [
                    new LadderComposeSlip
                    {
                        SlipNumber = bands[0].SlipNumber,
                        PredictionIds = [first.PredictionId]
                    }
                ]
            });
        }
    }

    private sealed class CollapseScoreAdvisor : FakeAdvisor
    {
        public IReadOnlyList<BankerPickRequest>? LastBankerCandidates { get; private set; }

        public override Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
            IReadOnlyList<BetslipScreenRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new BetslipScreenResult
            {
                Passed = candidates.Select(c => new BetslipScreenPick
                {
                    PredictionId = c.PredictionId,
                    Score = c.PredictionCategory == "Over2.5Goals" ? 99 : 10
                }).ToList()
            });

        public override Task<BankerPickResult> SelectBankerPicksAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default)
        {
            LastBankerCandidates = candidates.ToList();
            return base.SelectBankerPicksAsync(candidates, minOdds, maxOdds, ct);
        }
    }
}
