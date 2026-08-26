using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class TeamMatchStatsAndCoverageTests
{
    [Fact]
    public async Task SyncFromMatchScoresAsync_WritesGoalRows_WithNullXg()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var context = new ApplicationDbContext(options);
        context.MatchScores.Add(new MatchScore
        {
            HomeTeam = "Aces",
            AwayTeam = "Bears",
            League = "League",
            Score = "2-1",
            MatchTime = DateTime.UtcNow.AddDays(-2),
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)),
            HomeTeamKey = "aces",
            AwayTeamKey = "bears",
            LeagueKey = "league",
            IsLive = false
        });
        await context.SaveChangesAsync();

        var sync = new TeamMatchStatsSyncService(context, NullLogger<TeamMatchStatsSyncService>.Instance);
        var inserted = await sync.SyncFromMatchScoresAsync();

        Assert.Equal(2, inserted);
        var rows = await context.TeamMatchStats.OrderByDescending(row => row.IsHome).ToListAsync();
        Assert.All(rows, row => Assert.Null(row.ExpectedGoalsFor));
        Assert.Equal((short)2, rows.Single(row => row.IsHome).GoalsFor);
        Assert.Equal((short)1, rows.Single(row => !row.IsHome).GoalsFor);
    }

    [Fact]
    public async Task MlXgFeatureReadiness_BlocksSchemaBump_WhenXgMissing()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var context = new ApplicationDbContext(options);
        context.TeamMatchStats.Add(new TeamMatchStats
        {
            KickoffUtc = DateTime.UtcNow.AddDays(-1),
            MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            LeagueKey = "league",
            IsHome = true,
            TeamName = "Aces",
            OpponentName = "Bears",
            League = "League",
            GoalsFor = 1,
            GoalsAgainst = 0,
            SourceName = "FlashScore",
            SourceMatchId = "test-1",
            ObservedAtUtc = DateTime.UtcNow,
            AvailableFromUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var readiness = new MlXgFeatureReadiness(context);
        var result = await readiness.EvaluateAsync();

        Assert.False(result.MayBumpFeatureSchema);
        Assert.Equal(0, result.WithXg);
        Assert.Contains("Do not change", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatisticalCoverageDiagnostics_ReportsZeroXgCoverage()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var context = new ApplicationDbContext(options);
        var diagnostics = new StatisticalCoverageDiagnostics(
            context,
            NullLogger<StatisticalCoverageDiagnostics>.Instance);

        var report = await diagnostics.MeasureAsync();

        Assert.Equal(0, report.XgCoverage);
        Assert.False(report.XgFeatureSchemaReady);
        Assert.Contains("SofaScore", report.Notes, StringComparison.OrdinalIgnoreCase);
    }
}
