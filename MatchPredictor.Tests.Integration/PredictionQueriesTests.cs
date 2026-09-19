using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class PredictionQueriesTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task GetBTTSAsync_ReturnsOnlyCurrentRevisionForCanonicalLocalDate()
    {
        await using var context = CreateContext();
        var matchLocalDate = new DateOnly(2030, 1, 1);
        var kickoffTime = new TimeOnly(12, 0);
        var fixtureKey = "2030-01-01|test-league|home|away";

        context.Predictions.AddRange(
            new Prediction
            {
                Date = "01-01-2030",
                Time = "11:45",
                MatchLocalDate = matchLocalDate,
                MatchLocalTime = new TimeOnly(11, 45),
                FixtureKey = fixtureKey,
                League = "TestLeague",
                HomeTeam = "Home",
                AwayTeam = "Away",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "00:35 WAT",
                RunReason = "prewarm",
                IsCurrentRevision = false,
                RevisionNumber = 1
            },
            new Prediction
            {
                Date = "01-01-2030",
                Time = "12:00",
                MatchLocalDate = matchLocalDate,
                MatchLocalTime = kickoffTime,
                FixtureKey = fixtureKey,
                League = "TestLeague",
                HomeTeam = "Home",
                AwayTeam = "Away",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "04:30 WAT",
                RunReason = "refresh",
                IsCurrentRevision = true,
                RevisionNumber = 2
            });

        await context.SaveChangesAsync();
        var queries = new PredictionQueries(context);

        var results = await queries.GetBTTSAsync(matchLocalDate.ToDateTime(kickoffTime));

        var result = Assert.Single(results);
        Assert.Equal("12:00", result.Time);
        Assert.True(result.IsCurrentRevision);
        Assert.Equal(2, result.RevisionNumber);
    }

    [Fact]
    public async Task GetBTTSAsync_ExcludesBookingsFixtures()
    {
        await using var context = CreateContext();
        var matchLocalDate = new DateOnly(2030, 1, 3);
        var kickoffTime = new TimeOnly(14, 0);

        context.Predictions.AddRange(
            new Prediction
            {
                Date = "03-01-2030",
                Time = "14:00",
                MatchLocalDate = matchLocalDate,
                MatchLocalTime = kickoffTime,
                League = "MLS",
                HomeTeam = "Charlotte FC (Bookings)",
                AwayTeam = "Columbus Crew (Bookings)",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "00:35 WAT",
                RunReason = "refresh",
                IsCurrentRevision = true,
                RevisionNumber = 1
            },
            new Prediction
            {
                Date = "03-01-2030",
                Time = "15:00",
                MatchLocalDate = matchLocalDate,
                MatchLocalTime = new TimeOnly(15, 0),
                League = "MLS",
                HomeTeam = "Charlotte FC",
                AwayTeam = "Columbus Crew",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "00:35 WAT",
                RunReason = "refresh",
                IsCurrentRevision = true,
                RevisionNumber = 1
            });

        await context.SaveChangesAsync();
        var queries = new PredictionQueries(context);

        var results = await queries.GetBTTSAsync(matchLocalDate.ToDateTime(kickoffTime));

        var result = Assert.Single(results);
        Assert.Equal("Charlotte FC", result.HomeTeam);
        Assert.Equal("Columbus Crew", result.AwayTeam);
    }

    [Fact]
    public async Task GetRecentSettledPublishedAsync_ReturnsOnlySettledPublishedCurrentRevisions()
    {
        await using var context = CreateContext();
        var today = DateOnly.FromDateTime(MatchPredictor.Infrastructure.Utils.DateTimeProvider.GetLocalTime());
        var recent = today.AddDays(-2);

        context.Predictions.AddRange(
            new Prediction
            {
                Date = recent.ToString("dd-MM-yyyy"),
                Time = "15:00",
                MatchLocalDate = recent,
                MatchLocalTime = new TimeOnly(15, 0),
                League = "TestLeague",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                WasPublished = true,
                IsCurrentRevision = true,
                ActualScore = "2-1",
                ActualOutcome = "BTTS",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "04:30 WAT",
                RunReason = "refresh",
                RevisionNumber = 1
            },
            new Prediction
            {
                Date = recent.ToString("dd-MM-yyyy"),
                Time = "16:00",
                MatchLocalDate = recent,
                MatchLocalTime = new TimeOnly(16, 0),
                League = "TestLeague",
                HomeTeam = "Gamma",
                AwayTeam = "Delta",
                PredictionCategory = "Over2.5Goals",
                PredictedOutcome = "Over 2.5",
                WasPublished = false,
                IsCurrentRevision = true,
                ActualScore = "3-1",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "04:30 WAT",
                RunReason = "refresh",
                RevisionNumber = 1
            },
            new Prediction
            {
                Date = recent.ToString("dd-MM-yyyy"),
                Time = "17:00",
                MatchLocalDate = recent,
                MatchLocalTime = new TimeOnly(17, 0),
                League = "TestLeague",
                HomeTeam = "Epsilon",
                AwayTeam = "Zeta",
                PredictionCategory = "StraightWin",
                PredictedOutcome = "Home Win",
                WasPublished = true,
                IsCurrentRevision = true,
                ActualScore = null,
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "04:30 WAT",
                RunReason = "refresh",
                RevisionNumber = 1
            },
            new Prediction
            {
                Date = recent.ToString("dd-MM-yyyy"),
                Time = "18:00",
                MatchLocalDate = recent,
                MatchLocalTime = new TimeOnly(18, 0),
                League = "MLS",
                HomeTeam = "Charlotte FC (Bookings)",
                AwayTeam = "Columbus Crew (Bookings)",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                WasPublished = true,
                IsCurrentRevision = true,
                ActualScore = "1-1",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "04:30 WAT",
                RunReason = "refresh",
                RevisionNumber = 1
            },
            new Prediction
            {
                Date = recent.ToString("dd-MM-yyyy"),
                Time = "19:00",
                MatchLocalDate = recent,
                MatchLocalTime = new TimeOnly(19, 0),
                League = "TestLeague",
                HomeTeam = "Superseded",
                AwayTeam = "Side",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                WasPublished = true,
                IsCurrentRevision = false,
                ActualScore = "0-0",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "04:30 WAT",
                RunReason = "refresh",
                RevisionNumber = 1
            });

        await context.SaveChangesAsync();
        var queries = new PredictionQueries(context);

        var results = await queries.GetRecentSettledPublishedAsync(30);

        Assert.Contains(results, p => p.HomeTeam == "Alpha" && p.AwayTeam == "Beta");
        Assert.DoesNotContain(results, p => p.HomeTeam == "Gamma");
        Assert.DoesNotContain(results, p => p.HomeTeam == "Epsilon");
        Assert.DoesNotContain(results, p => p.HomeTeam == "Superseded");
        Assert.DoesNotContain(results, p => p.HomeTeam.Contains("Bookings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRecentSettledPublishedAsync_ExcludesRowsOutsideDayWindow()
    {
        await using var context = CreateContext();
        var localNow = MatchPredictor.Infrastructure.Utils.DateTimeProvider.GetLocalTime();
        var today = DateOnly.FromDateTime(localNow);
        var inside = today.AddDays(-5);
        var outside = today.AddDays(-45);

        context.Predictions.AddRange(
            new Prediction
            {
                Date = inside.ToString("dd-MM-yyyy"),
                Time = "15:00",
                MatchLocalDate = inside,
                MatchLocalTime = new TimeOnly(15, 0),
                League = "TestLeague",
                HomeTeam = "Inside Home",
                AwayTeam = "Inside Away",
                PredictionCategory = "Under2.5Goals",
                PredictedOutcome = "Under 2.5",
                WasPublished = true,
                IsCurrentRevision = true,
                ActualScore = "0-1",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "04:30 WAT",
                RunReason = "refresh",
                RevisionNumber = 1
            },
            new Prediction
            {
                Date = outside.ToString("dd-MM-yyyy"),
                Time = "15:00",
                MatchLocalDate = outside,
                MatchLocalTime = new TimeOnly(15, 0),
                League = "TestLeague",
                HomeTeam = "Outside Home",
                AwayTeam = "Outside Away",
                PredictionCategory = "Under2.5Goals",
                PredictedOutcome = "Under 2.5",
                WasPublished = true,
                IsCurrentRevision = true,
                ActualScore = "1-0",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "04:30 WAT",
                RunReason = "refresh",
                RevisionNumber = 1
            });

        await context.SaveChangesAsync();
        var queries = new PredictionQueries(context);

        var results = await queries.GetRecentSettledPublishedAsync(30);

        Assert.Contains(results, p => p.HomeTeam == "Inside Home");
        Assert.DoesNotContain(results, p => p.HomeTeam == "Outside Home");
    }
}
