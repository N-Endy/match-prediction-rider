using MatchPredictor.Infrastructure.Statistics;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class EloRatingModelTests
{
    [Fact]
    public void Train_RaisesRatingOfConsistentWinner()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var results = new List<MatchResult>();
        for (var i = 0; i < 20; i++)
        {
            // "Strong" beats "Weak" repeatedly, alternating venues.
            results.Add(i % 2 == 0
                ? new MatchResult("Strong", "Weak", 2, 0, start.AddDays(i))
                : new MatchResult("Weak", "Strong", 0, 2, start.AddDays(i)));
        }

        var model = new EloRatingModel().Train(results);

        Assert.True(model.GetRating("Strong") > model.GetRating("Weak"));
    }

    [Fact]
    public void PredictResult_ProducesNormalizedDistribution()
    {
        var model = new EloRatingModel();
        model.Update(new MatchResult("Strong", "Weak", 3, 0, DateTime.UtcNow));

        var (homeWin, draw, awayWin) = model.PredictResult("Strong", "Weak");

        Assert.Equal(1.0, homeWin + draw + awayWin, 9);
        Assert.All(new[] { homeWin, draw, awayWin }, value => Assert.InRange(value, 0.0, 1.0));
    }

    [Fact]
    public void PredictResult_FavoursStrongerSide()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var results = Enumerable.Range(0, 10)
            .Select(i => new MatchResult("Strong", "Weak", 3, 0, start.AddDays(i)))
            .ToList();
        var model = new EloRatingModel().Train(results);

        var (strongHome, _, weakAway) = model.PredictResult("Strong", "Weak");

        Assert.True(strongHome > weakAway);
    }

    [Fact]
    public void ExpectedScore_IsHalfForEqualRatings()
    {
        var model = new EloRatingModel();

        var expected = model.ExpectedScore(1500, 1500);

        Assert.Equal(0.5, expected, 9);
    }

    [Fact]
    public void HomeAdvantage_GivesUnseenHomeTeamTheEdge()
    {
        var model = new EloRatingModel(new EloOptions { HomeAdvantage = 80 });

        var (homeWin, _, awayWin) = model.PredictResult("A", "B");

        Assert.True(homeWin > awayWin);
    }

    [Fact]
    public void Update_MassiveUnderdogUpset_DoesNotDivideByZeroOrInvertMultiplier()
    {
        var model = new EloRatingModel();
        // Train team A to very high and team B to very low, creating a > 2200 point gap
        var results = new List<MatchResult>();
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 80; i++)
        {
            results.Add(new MatchResult("SuperTeam", "Fodder", 5, 0, date.AddDays(i)));
        }
        model.Train(results);

        var superRatingBefore = model.GetRating("SuperTeam");
        var underdogRatingBefore = model.GetRating("Underdog"); // starts at initial 1500

        // Massive gap: SuperTeam is > 3000, Underdog is 1500 (gap > 1500..2500)
        // Underdog pulls off an upset 4-0
        model.Update(new MatchResult("Underdog", "SuperTeam", 4, 0, date.AddDays(100)));

        var underdogRatingAfter = model.GetRating("Underdog");
        var superRatingAfter = model.GetRating("SuperTeam");

        Assert.False(double.IsNaN(underdogRatingAfter));
        Assert.False(double.IsInfinity(underdogRatingAfter));
        Assert.False(double.IsNaN(superRatingAfter));
        Assert.False(double.IsInfinity(superRatingAfter));
        // Underdog MUST gain rating points for winning!
        Assert.True(underdogRatingAfter > underdogRatingBefore);
        // SuperTeam MUST lose rating points for losing!
        Assert.True(superRatingAfter < superRatingBefore);
    }
}
