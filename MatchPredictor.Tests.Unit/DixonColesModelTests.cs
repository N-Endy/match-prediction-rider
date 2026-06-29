using MatchPredictor.Infrastructure.Statistics;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class DixonColesModelTests
{
    private static List<MatchResult> BuildSyntheticLeague(DateTime start)
    {
        // Four teams with a clear strength ordering: Aces > Bears > Cats > Dogs.
        // Generate a deterministic double round-robin repeated several times.
        var teams = new[] { "Aces", "Bears", "Cats", "Dogs" };
        var strength = new Dictionary<string, int>
        {
            ["Aces"] = 3,
            ["Bears"] = 2,
            ["Cats"] = 1,
            ["Dogs"] = 0
        };

        var results = new List<MatchResult>();
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

                    // Stronger team scores more; home gets a small bump.
                    var homeGoals = Math.Max(0, strength[home] - strength[away] + 1);
                    var awayGoals = Math.Max(0, strength[away] - strength[home] + 1);
                    results.Add(new MatchResult(home, away, homeGoals, awayGoals, start.AddDays(day++)));
                }
            }
        }

        return results;
    }

    [Fact]
    public void Fit_LearnsTeamStrengthOrdering()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var model = DixonColesModel.Fit(BuildSyntheticLeague(start), start.AddDays(400));

        Assert.True(model.GetAttack("Aces") > model.GetAttack("Dogs"));
        Assert.True(model.GetDefence("Aces") > model.GetDefence("Dogs"));
    }

    [Fact]
    public void Predict_ProducesConsistentProbabilities()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var model = DixonColesModel.Fit(BuildSyntheticLeague(start), start.AddDays(400));

        var prediction = model.Predict("Aces", "Dogs");

        // 1X2 must sum to ~1.
        Assert.Equal(1.0, prediction.HomeWin + prediction.Draw + prediction.AwayWin, 6);
        // Over + Under must sum to 1.
        Assert.Equal(1.0, prediction.Over25 + prediction.Under25, 6);
        Assert.All(
            new[] { prediction.HomeWin, prediction.Draw, prediction.AwayWin, prediction.Over25, prediction.Btts },
            value => Assert.InRange(value, 0.0, 1.0));
    }

    [Fact]
    public void Predict_FavoursStrongerHomeTeam()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var model = DixonColesModel.Fit(BuildSyntheticLeague(start), start.AddDays(400));

        var strongHome = model.Predict("Aces", "Dogs");

        Assert.True(strongHome.HomeWin > strongHome.AwayWin);
        Assert.True(strongHome.HomeWin > 0.5);
    }

    [Fact]
    public void HasTeam_RequiresMinimumHistory()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var results = new List<MatchResult>
        {
            new("Aces", "Bears", 2, 1, start),
            new("Bears", "Aces", 0, 1, start.AddDays(1))
        };

        var model = DixonColesModel.Fit(results, start.AddDays(10), new DixonColesOptions { MinMatchesPerTeam = 4 });

        Assert.False(model.HasTeam("Aces"));
        Assert.False(model.HasTeam("Nonexistent"));
    }

    [Fact]
    public void Rho_StaysWithinSearchedRange()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var model = DixonColesModel.Fit(BuildSyntheticLeague(start), start.AddDays(400));

        Assert.InRange(model.Rho, -0.2, 0.2);
    }

    [Fact]
    public void Fit_WithNoHistory_ReturnsUsableBaseline()
    {
        var model = DixonColesModel.Fit([], DateTime.UtcNow);
        var prediction = model.Predict("A", "B");

        Assert.Equal(1.0, prediction.HomeWin + prediction.Draw + prediction.AwayWin, 6);
    }
}
