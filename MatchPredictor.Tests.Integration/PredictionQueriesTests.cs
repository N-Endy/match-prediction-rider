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
    public async Task GetCombinedSampleAsync_UsesCurrentRevisionsWithoutInMemoryTimeRepair()
    {
        await using var context = CreateContext();
        var matchLocalDate = new DateOnly(2030, 1, 2);
        var fixtureKey = "2030-01-02|league|alpha|beta";

        context.Predictions.AddRange(
            new Prediction
            {
                Date = "02-01-2030",
                Time = "09:00",
                MatchLocalDate = matchLocalDate,
                MatchLocalTime = new TimeOnly(9, 0),
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "StraightWin",
                PredictedOutcome = "Home Win",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "00:35 WAT",
                RunReason = "initial",
                IsCurrentRevision = true,
                RevisionNumber = 1
            },
            new Prediction
            {
                Date = "02-01-2030",
                Time = "09:15",
                MatchLocalDate = matchLocalDate,
                MatchLocalTime = new TimeOnly(9, 15),
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "Over2.5Goals",
                PredictedOutcome = "Over 2.5",
                PredictionRunId = Guid.NewGuid(),
                RunLabel = "00:35 WAT",
                RunReason = "initial",
                IsCurrentRevision = true,
                RevisionNumber = 1
            });

        await context.SaveChangesAsync();
        var queries = new PredictionQueries(context);

        var results = await queries.GetCombinedSampleAsync(matchLocalDate.ToDateTime(new TimeOnly(0, 0)), 10);

        var result = Assert.Single(results);
        Assert.Equal("09:00", result.Time);
        Assert.Equal(new TimeOnly(9, 0), result.MatchLocalTime);
    }
}
