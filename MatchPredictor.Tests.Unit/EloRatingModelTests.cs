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
}
