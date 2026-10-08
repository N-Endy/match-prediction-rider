using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class MarketTimingServiceTests
{
    private readonly ApplicationDbContext _db;
    private readonly FakePricingService _pricing;
    private readonly MarketTimingService _service;

    public MarketTimingServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new ApplicationDbContext(options);
        _pricing = new FakePricingService();
        _service = new MarketTimingService(_db, _pricing, NullLogger<MarketTimingService>.Instance);
    }

    [Fact]
    public async Task EvaluateOddsTrajectory_SharpDrop_TriggersSteamMoveUrgentTakeNow()
    {
        var fixtureKey = "Arsenal|Chelsea|EPL";
        var kickoff = DateTime.UtcNow.AddHours(2);

        var prediction = new Prediction
        {
            Id = 1,
            Date = "03-10-2026",
            Time = "15:00",
            League = "EPL",
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            FixtureKey = fixtureKey,
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            MatchDateTime = kickoff,
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WasPublished = true,
            IsCurrentRevision = true
        };
        _db.Predictions.Add(prediction);

        // Baseline published odds was 2.20 (45.4% implied) captured 2 hours ago
        _db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 1,
            Market = "StraightWin",
            Outcome = "Home Win",
            DecimalOdds = 2.20,
            ImpliedProbability = 1.0 / 2.20,
            SnapshotKind = PredictionOddsSnapshotKind.Publish,
            CapturedAtUtc = DateTime.UtcNow.AddHours(-2)
        });
        await _db.SaveChangesAsync();

        // Current price collapsed to 1.80 (55.5% implied -> +10.1% change, odds drop -0.40)
        var advisory = await _service.EvaluateOddsTrajectoryAsync(
            fixtureKey, "StraightWin", "Home Win", currentOdds: 1.80, kickoff);

        Assert.Equal(TimingSignal.UrgentTakeNow, advisory.Signal);
        Assert.True(advisory.IsSteamMove);
        Assert.False(advisory.IsLineupDriven);
        Assert.True(advisory.OddsVelocityPerHour < 0);
        Assert.Contains("Steam Move", advisory.Summary);
    }

    [Fact]
    public async Task EvaluateOddsTrajectory_LineupDrivenSteamMove_CouplesLineupAndSteamSignals()
    {
        var fixtureKey = "ManCity|Liverpool|EPL";
        var kickoff = DateTime.UtcNow.AddMinutes(50);

        var prediction = new Prediction
        {
            Id = 2,
            Date = "03-10-2026",
            Time = "15:00",
            League = "EPL",
            HomeTeam = "ManCity",
            AwayTeam = "Liverpool",
            FixtureKey = fixtureKey,
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            MatchDateTime = kickoff,
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WasPublished = true,
            IsCurrentRevision = true
        };
        _db.Predictions.Add(prediction);

        _db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 2,
            Market = "StraightWin",
            Outcome = "Home Win",
            DecimalOdds = 2.10,
            ImpliedProbability = 1.0 / 2.10,
            SnapshotKind = PredictionOddsSnapshotKind.Publish,
            CapturedAtUtc = DateTime.UtcNow.AddHours(-3)
        });

        // Add confirmed lineup snapshot
        _db.MatchLineupSnapshots.Add(new MatchLineupSnapshot
        {
            FixtureKey = fixtureKey,
            IsConfirmed = true,
            HomeAttackAdjustment = 0.15,
            CapturedAtUtc = DateTime.UtcNow.AddMinutes(-40)
        });
        await _db.SaveChangesAsync();

        // Price collapses to 1.75
        var advisory = await _service.EvaluateOddsTrajectoryAsync(
            fixtureKey, "StraightWin", "Home Win", currentOdds: 1.75, kickoff);

        Assert.Equal(TimingSignal.UrgentTakeNow, advisory.Signal);
        Assert.True(advisory.IsSteamMove);
        Assert.True(advisory.IsLineupDriven);
        Assert.Contains("Lineup-Driven Steam Move", advisory.Summary);
    }

    [Fact]
    public async Task EvaluateOddsTrajectory_DriftingPrice_TriggersDriftingWait()
    {
        var fixtureKey = "Barca|RealMadrid|LaLiga";
        var kickoff = DateTime.UtcNow.AddHours(5);

        var prediction = new Prediction
        {
            Id = 3,
            Date = "03-10-2026",
            Time = "15:00",
            League = "LaLiga",
            HomeTeam = "Barca",
            AwayTeam = "RealMadrid",
            FixtureKey = fixtureKey,
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            MatchDateTime = kickoff,
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WasPublished = true,
            IsCurrentRevision = true
        };
        _db.Predictions.Add(prediction);

        // Baseline published odds was 2.00 captured 4 hours ago
        _db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 3,
            Market = "StraightWin",
            Outcome = "Home Win",
            DecimalOdds = 2.00,
            ImpliedProbability = 1.0 / 2.00,
            SnapshotKind = PredictionOddsSnapshotKind.Publish,
            CapturedAtUtc = DateTime.UtcNow.AddHours(-4)
        });
        await _db.SaveChangesAsync();

        // Price lengthened to 2.30 (+0.30 rise)
        var advisory = await _service.EvaluateOddsTrajectoryAsync(
            fixtureKey, "StraightWin", "Home Win", currentOdds: 2.30, kickoff);

        Assert.Equal(TimingSignal.DriftingWait, advisory.Signal);
        Assert.False(advisory.IsSteamMove);
        Assert.True(advisory.OddsVelocityPerHour > 0);
        Assert.Contains("Drifting Price", advisory.Summary);
    }

    [Fact]
    public async Task EvaluateOddsTrajectory_StablePrice_ReturnsStableSignal()
    {
        var fixtureKey = "Bayern|Dortmund|Bundesliga";
        var kickoff = DateTime.UtcNow.AddHours(6);

        var prediction = new Prediction
        {
            Id = 4,
            Date = "03-10-2026",
            Time = "15:00",
            League = "Bundesliga",
            HomeTeam = "Bayern",
            AwayTeam = "Dortmund",
            FixtureKey = fixtureKey,
            PredictionCategory = "Over25Goals",
            PredictedOutcome = "Over 2.5",
            MatchDateTime = kickoff,
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WasPublished = true,
            IsCurrentRevision = true
        };
        _db.Predictions.Add(prediction);

        _db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 4,
            Market = "Over25Goals",
            Outcome = "Over 2.5",
            DecimalOdds = 1.80,
            ImpliedProbability = 1.0 / 1.80,
            SnapshotKind = PredictionOddsSnapshotKind.Publish,
            CapturedAtUtc = DateTime.UtcNow.AddHours(-5)
        });
        await _db.SaveChangesAsync();

        // Price barely shifted: 1.81
        var advisory = await _service.EvaluateOddsTrajectoryAsync(
            fixtureKey, "Over25Goals", "Over 2.5", currentOdds: 1.81, kickoff);

        Assert.Equal(TimingSignal.Stable, advisory.Signal);
        Assert.False(advisory.IsSteamMove);
        Assert.Contains("Stable Market Price", advisory.Summary);
    }

    [Fact]
    public async Task CaptureInterimOddsSnapshotsAsync_SavesInitialInterimSnapshot()
    {
        var kickoff = DateTime.UtcNow.AddHours(3);
        var prediction = new Prediction
        {
            Id = 10,
            Date = "08-10-2026",
            Time = "15:00",
            League = "EPL",
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            FixtureKey = "Arsenal|Chelsea|EPL",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            MatchDateTime = kickoff,
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WasPublished = true,
            IsCurrentRevision = true
        };
        _db.Predictions.Add(prediction);
        await _db.SaveChangesAsync();

        _pricing.Fixtures =
        [
            new SourceMarketFixture
            {
                HomeTeam = "Arsenal",
                AwayTeam = "Chelsea",
                League = "EPL",
                MatchTimeUtc = kickoff,
                HomeWinOdds = 1.95
            }
        ];

        await _service.CaptureInterimOddsSnapshotsAsync();

        var snapshots = await _db.PredictionOddsSnapshots
            .Where(s => s.PredictionId == 10 && s.SnapshotKind == PredictionOddsSnapshotKind.Interim)
            .ToListAsync();

        Assert.Single(snapshots);
        Assert.Equal(1.95, snapshots[0].DecimalOdds);
        Assert.Equal("SportyBet", snapshots[0].SourceName);
    }

    [Fact]
    public async Task CaptureInterimOddsSnapshotsAsync_WithinTwoHours_SkipsSnapshot()
    {
        var kickoff = DateTime.UtcNow.AddHours(4);
        var prediction = new Prediction
        {
            Id = 11,
            Date = "08-10-2026",
            Time = "15:00",
            League = "EPL",
            HomeTeam = "Liverpool",
            AwayTeam = "Everton",
            FixtureKey = "Liverpool|Everton|EPL",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            MatchDateTime = kickoff,
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WasPublished = true,
            IsCurrentRevision = true
        };
        _db.Predictions.Add(prediction);
        _db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 11,
            SourceName = "SportyBet",
            Market = "StraightWin",
            Outcome = "Home Win",
            DecimalOdds = 1.50,
            ImpliedProbability = 1.0 / 1.50,
            SnapshotKind = PredictionOddsSnapshotKind.Interim,
            CapturedAtUtc = DateTime.UtcNow.AddMinutes(-45)
        });
        await _db.SaveChangesAsync();

        _pricing.Fixtures =
        [
            new SourceMarketFixture
            {
                HomeTeam = "Liverpool",
                AwayTeam = "Everton",
                League = "EPL",
                MatchTimeUtc = kickoff,
                HomeWinOdds = 1.48
            }
        ];

        await _service.CaptureInterimOddsSnapshotsAsync();

        var snapshots = await _db.PredictionOddsSnapshots
            .Where(s => s.PredictionId == 11 && s.SnapshotKind == PredictionOddsSnapshotKind.Interim)
            .ToListAsync();

        Assert.Single(snapshots);
        Assert.Equal(1.50, snapshots[0].DecimalOdds);
    }

    [Fact]
    public async Task CaptureInterimOddsSnapshotsAsync_AfterTwoHours_RecordsSubsequentSnapshot()
    {
        var kickoff = DateTime.UtcNow.AddHours(5);
        var prediction = new Prediction
        {
            Id = 12,
            Date = "08-10-2026",
            Time = "15:00",
            League = "La Liga",
            HomeTeam = "Real Madrid",
            AwayTeam = "Barcelona",
            FixtureKey = "Real Madrid|Barcelona|La Liga",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            MatchDateTime = kickoff,
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WasPublished = true,
            IsCurrentRevision = true
        };
        _db.Predictions.Add(prediction);
        _db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 12,
            SourceName = "SportyBet",
            Market = "StraightWin",
            Outcome = "Home Win",
            DecimalOdds = 2.10,
            ImpliedProbability = 1.0 / 2.10,
            SnapshotKind = PredictionOddsSnapshotKind.Interim,
            CapturedAtUtc = DateTime.UtcNow.AddHours(-3)
        });
        await _db.SaveChangesAsync();

        _pricing.Fixtures =
        [
            new SourceMarketFixture
            {
                HomeTeam = "Real Madrid",
                AwayTeam = "Barcelona",
                League = "La Liga",
                MatchTimeUtc = kickoff,
                HomeWinOdds = 1.85
            }
        ];

        await _service.CaptureInterimOddsSnapshotsAsync();

        var snapshots = await _db.PredictionOddsSnapshots
            .Where(s => s.PredictionId == 12 && s.SnapshotKind == PredictionOddsSnapshotKind.Interim)
            .OrderBy(s => s.CapturedAtUtc)
            .ToListAsync();

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(2.10, snapshots[0].DecimalOdds);
        Assert.Equal(1.85, snapshots[1].DecimalOdds);
    }

    private sealed class FakePricingService : ISourceMarketPricingService
    {
        public IReadOnlyList<SourceMarketFixture> Fixtures { get; set; } = [];

        public Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default)
        {
            return Task.FromResult(Fixtures);
        }

        public Task<IReadOnlyList<SourceMarketFixture>> GetSourceMarketFixturesForDateAsync(DateOnly targetLocalDate, CancellationToken ct = default)
        {
            return Task.FromResult(Fixtures);
        }
    }
}
