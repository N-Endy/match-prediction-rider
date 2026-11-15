using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class OperationsTipsterServiceTests
{
    private sealed class StubSportyBetBookingService : ISportyBetBookingService
    {
        public Task<BookingResult> BookGamesAsync(List<BookingSelection> selections)
        {
            return Task.FromResult(new BookingResult
            {
                Success = true,
                BookingCode = "B77XYZ",
                BookingUrl = "https://www.sportybet.com/ng/?shareCode=B77XYZ",
                BookedCount = selections.Count
            });
        }
    }

    private static ApplicationDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task GetOperationsDossierAsync_WhenNoFixturesOnSlate_PreservesAllPillarsAndGradesC()
    {
        await using var db = CreateInMemoryDb();
        var bookingService = new StubSportyBetBookingService();
        var service = new OperationsTipsterService(db, bookingService, NullLogger<OperationsTipsterService>.Instance);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dossier = await service.GetOperationsDossierAsync(today);

        Assert.NotNull(dossier);
        Assert.Equal(today, dossier.SlateDate);
        Assert.Equal(0, dossier.TotalFixturesAnalyzed);
        Assert.Equal(0, dossier.PublishedPicksCount);
        Assert.Contains("GRADE C", dossier.SlateGrade);
        Assert.Equal(5.0m, dossier.TotalDailyRiskBudgetPercent);

        Assert.Equal(3, dossier.Tickets.Count);
        Assert.All(dossier.Tickets, ticket =>
        {
            Assert.True(ticket.IsPreserved);
            Assert.Equal(0m, ticket.RecommendedUnits);
            Assert.Equal(0m, ticket.AllocationPercent);
            Assert.NotEmpty(ticket.PreservationReason);
            Assert.Empty(ticket.Legs);
        });
    }

    [Fact]
    public async Task GetOperationsDossierAsync_WithHighConfidenceBankers_BuildsCompounderPillar()
    {
        await using var db = CreateInMemoryDb();
        var bookingService = new StubSportyBetBookingService();
        var service = new OperationsTipsterService(db, bookingService, NullLogger<OperationsTipsterService>.Instance);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Add 10 fixtures so slate qualifies for Grade B
        for (int i = 1; i <= 10; i++)
        {
            var pred = new Prediction
            {
                Id = i,
                FixtureKey = $"fx-{i}",
                HomeTeam = $"Home {i}",
                AwayTeam = $"Away {i}",
                League = "Premier League",
                MatchLocalDate = today,
                MatchDateTime = DateTime.UtcNow.AddHours(2),
                Date = today.ToString("yyyy-MM-dd"),
                Time = "15:00",
                PredictionCategory = "StraightWin",
                PredictedOutcome = $"Home {i} Win",
                ConfidenceScore = i == 1 ? 0.78m : 0.68m,
                IsCurrentRevision = true,
                WasPublished = true
            };
            db.Predictions.Add(pred);

            // Add real snapshot odds for the first prediction
            db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
            {
                PredictionId = i,
                SourceName = "SportyBet",
                Market = "1X2",
                Outcome = "1",
                DecimalOdds = i == 1 ? 1.65 : 1.45,
                CapturedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        var dossier = await service.GetOperationsDossierAsync(today);

        Assert.NotNull(dossier);
        Assert.Contains("GRADE B", dossier.SlateGrade);

        var compounder = dossier.Tickets.First(t => t.PillarKind == OperationsPillarKind.Compounder);
        Assert.False(compounder.IsPreserved);
        Assert.Equal(60m, compounder.AllocationPercent);
        Assert.Equal(3.0m, compounder.RecommendedUnits);
        Assert.Equal(0.25m, compounder.KellyFraction);
        Assert.InRange(compounder.CombinedOdds, 1.50m, 2.30m);
        Assert.NotEmpty(compounder.Legs);
        Assert.Equal("B77XYZ", compounder.BookingCode);
    }

    [Fact]
    public async Task GetOperationsDossierAsync_WithDislocatedOdds_BuildsValueEdgePillar()
    {
        await using var db = CreateInMemoryDb();
        var bookingService = new StubSportyBetBookingService();
        var service = new OperationsTipsterService(db, bookingService, NullLogger<OperationsTipsterService>.Instance);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Add 8 predictions with high model probabilities and underpriced bookmaker odds (positive edge >= +3.5%)
        for (int i = 1; i <= 8; i++)
        {
            var pred = new Prediction
            {
                Id = 100 + i,
                FixtureKey = $"value-fx-{i}",
                HomeTeam = $"Club {i}",
                AwayTeam = $"Rival {i}",
                League = "La Liga",
                MatchLocalDate = today,
                MatchDateTime = DateTime.UtcNow.AddHours(3),
                Date = today.ToString("yyyy-MM-dd"),
                Time = "18:00",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ConfidenceScore = 0.65m, // model 65%
                IsCurrentRevision = true,
                WasPublished = true
            };
            db.Predictions.Add(pred);

            // Bookmaker odds 1.75 => Implied prob 57.1% => Edge = +7.9%
            db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
            {
                PredictionId = 100 + i,
                SourceName = "SportyBet",
                Market = "BTTS",
                Outcome = "Yes",
                DecimalOdds = 1.75,
                CapturedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        var dossier = await service.GetOperationsDossierAsync(today);

        var valueEdge = dossier.Tickets.First(t => t.PillarKind == OperationsPillarKind.ValueEdge);
        Assert.False(valueEdge.IsPreserved);
        Assert.Equal(30m, valueEdge.AllocationPercent);
        Assert.Equal(1.5m, valueEdge.RecommendedUnits);
        Assert.Equal(0.20m, valueEdge.KellyFraction);
        Assert.True(valueEdge.CombinedOdds >= 3.0m);
        Assert.True(valueEdge.Legs.Count >= 2);
    }

    [Fact]
    public async Task GetOperationsDossierAsync_CompounderPillar_IncludesOver15AndDoubleChance1X_ExcludesDoubleChance12()
    {
        await using var db = CreateInMemoryDb();
        var bookingService = new StubSportyBetBookingService();
        var service = new OperationsTipsterService(db, bookingService, NullLogger<OperationsTipsterService>.Instance);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Add 10 fixtures so slate qualifies for Grade B
        for (int i = 1; i <= 10; i++)
        {
            var pred = new Prediction
            {
                Id = 200 + i,
                FixtureKey = $"compounder-fx-{i}",
                HomeTeam = $"Team H{i}",
                AwayTeam = $"Team A{i}",
                League = "Premier League",
                MatchLocalDate = today,
                MatchDateTime = DateTime.UtcNow.AddHours(2),
                Date = today.ToString("yyyy-MM-dd"),
                Time = "16:00",
                PredictionCategory = "StraightWin",
                PredictedOutcome = $"Team H{i} Win",
                ConfidenceScore = 0.55m,
                IsCurrentRevision = true,
                WasPublished = true
            };
            db.Predictions.Add(pred);
        }

        // Add Double Chance 12 with very high confidence (0.85m) - must be excluded from Compounder
        var dc12Pred = new Prediction
        {
            Id = 301,
            FixtureKey = "fx-dc12",
            HomeTeam = "Rivals A",
            AwayTeam = "Rivals B",
            League = "Premier League",
            MatchLocalDate = today,
            MatchDateTime = DateTime.UtcNow.AddHours(2),
            Date = today.ToString("yyyy-MM-dd"),
            Time = "16:00",
            PredictionCategory = "DoubleChance",
            PredictedOutcome = "12",
            ConfidenceScore = 0.85m,
            IsCurrentRevision = true,
            WasPublished = true
        };
        db.Predictions.Add(dc12Pred);
        db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 301,
            SourceName = "SportyBet",
            Market = "Double Chance",
            Outcome = "12",
            DecimalOdds = 1.30,
            CapturedAtUtc = DateTime.UtcNow
        });

        // Add Double Chance 1X with high confidence (0.80m) - eligible for Compounder
        var dc1XPred = new Prediction
        {
            Id = 302,
            FixtureKey = "fx-dc1x",
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            League = "Premier League",
            MatchLocalDate = today,
            MatchDateTime = DateTime.UtcNow.AddHours(2),
            Date = today.ToString("yyyy-MM-dd"),
            Time = "16:00",
            PredictionCategory = "DoubleChance",
            PredictedOutcome = "1X",
            ConfidenceScore = 0.80m,
            IsCurrentRevision = true,
            WasPublished = true
        };
        db.Predictions.Add(dc1XPred);
        db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 302,
            SourceName = "SportyBet",
            Market = "Double Chance",
            Outcome = "1X",
            DecimalOdds = 1.35,
            CapturedAtUtc = DateTime.UtcNow
        });

        // Add Over 1.5 Goals with high confidence (0.78m) - eligible for Compounder
        var over15Pred = new Prediction
        {
            Id = 303,
            FixtureKey = "fx-o15",
            HomeTeam = "Liverpool",
            AwayTeam = "Everton",
            League = "Premier League",
            MatchLocalDate = today,
            MatchDateTime = DateTime.UtcNow.AddHours(2),
            Date = today.ToString("yyyy-MM-dd"),
            Time = "16:00",
            PredictionCategory = "Over1.5Goals",
            PredictedOutcome = "Over 1.5",
            ConfidenceScore = 0.78m,
            IsCurrentRevision = true,
            WasPublished = true
        };
        db.Predictions.Add(over15Pred);
        db.PredictionOddsSnapshots.Add(new PredictionOddsSnapshot
        {
            PredictionId = 303,
            SourceName = "SportyBet",
            Market = "Over/Under",
            Outcome = "Over 1.5",
            DecimalOdds = 1.35,
            CapturedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        var dossier = await service.GetOperationsDossierAsync(today);

        var compounder = dossier.Tickets.First(t => t.PillarKind == OperationsPillarKind.Compounder);
        Assert.False(compounder.IsPreserved);
        Assert.NotEmpty(compounder.Legs);
        // Excludes Double Chance 12
        Assert.DoesNotContain(compounder.Legs, l => l.Market == "Double Chance" && (l.Prediction == "12" || l.Prediction == "1/2"));
        // Contains 1X and Over 1.5
        Assert.Contains(compounder.Legs, l => l.Market == "Double Chance" && l.Prediction == "1X");
        Assert.Contains(compounder.Legs, l => l.Market == "Over 1.5" && l.Prediction == "Over 1.5");
    }
}
