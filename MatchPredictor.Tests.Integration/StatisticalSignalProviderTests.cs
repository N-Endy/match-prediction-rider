using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class StatisticalSignalProviderTests
{
    private static ApplicationDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static List<MatchScore> BuildHistory(DateTime start)
    {
        var teams = new[] { "Alpha", "Beta", "Gamma" };
        var strength = new Dictionary<string, int> { ["Alpha"] = 2, ["Beta"] = 1, ["Gamma"] = 0 };
        var scores = new List<MatchScore>();
        var day = 0;
        for (var round = 0; round < 8; round++)
        {
            foreach (var home in teams)
            {
                foreach (var away in teams)
                {
                    if (home == away)
                    {
                        continue;
                    }

                    var homeGoals = Math.Max(0, strength[home] - strength[away] + 1);
                    var awayGoals = Math.Max(0, strength[away] - strength[home] + 1);
                    scores.Add(new MatchScore
                    {
                        League = "Test",
                        HomeTeam = home,
                        AwayTeam = away,
                        Score = $"{homeGoals}:{awayGoals}",
                        MatchTime = start.AddDays(day++),
                        IsLive = false
                    });
                }
            }
        }

        return scores;
    }

    [Fact]
    public void BuildSignals_ProducesSignalForTeamsWithHistory()
    {
        using var context = NewContext();
        var historyStart = DateTime.UtcNow.AddDays(-200);
        context.MatchScores.AddRange(BuildHistory(historyStart));
        context.SaveChanges();

        var provider = new StatisticalSignalProvider(context);
        var match = new MatchData
        {
            HomeTeam = "Alpha",
            AwayTeam = "Gamma",
            MatchDateTime = DateTime.UtcNow.AddDays(2)
        };

        var signals = provider.BuildSignals([match]);
        var signal = signals.GetSignal(match);

        Assert.NotNull(signal);
        Assert.Equal(1.0, signal!.HomeWin + signal.Draw + signal.AwayWin, 6);
        // The stronger home team should be favoured.
        Assert.True(signal.HomeWin > signal.AwayWin);
    }

    [Fact]
    public void BuildSignals_ReturnsNullForUnknownTeams()
    {
        using var context = NewContext();
        context.MatchScores.AddRange(BuildHistory(DateTime.UtcNow.AddDays(-200)));
        context.SaveChanges();

        var provider = new StatisticalSignalProvider(context);
        var match = new MatchData
        {
            HomeTeam = "Unknown FC",
            AwayTeam = "Mystery United",
            MatchDateTime = DateTime.UtcNow.AddDays(2)
        };

        var signals = provider.BuildSignals([match]);

        Assert.Null(signals.GetSignal(match));
    }

    [Fact]
    public void BuildSignals_ExcludesResultsAfterKickoff_PointInTime()
    {
        using var context = NewContext();
        // All "history" actually occurs in the future, after the fixture kickoff,
        // so the point-in-time guard must exclude every row and yield no signal.
        context.MatchScores.AddRange(BuildHistory(DateTime.UtcNow.AddDays(5)));
        context.SaveChanges();

        var provider = new StatisticalSignalProvider(context);
        var match = new MatchData
        {
            HomeTeam = "Alpha",
            AwayTeam = "Gamma",
            MatchDateTime = DateTime.UtcNow.AddDays(2)
        };

        var signals = provider.BuildSignals([match]);

        Assert.Null(signals.GetSignal(match));
    }
}
