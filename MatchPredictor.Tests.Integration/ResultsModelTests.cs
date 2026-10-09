using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Pages;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ResultsModelTests
{
    private sealed class FakePredictionQueries(IReadOnlyList<Prediction> predictions) : IPredictionQueries
    {
        public Task<IReadOnlyList<Prediction>> GetBTTSAsync(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetOver25Async(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetUnder25Async(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetRecentSettledPublishedAsync(int days = 30) => Task.FromResult(predictions);
    }

    [Fact]
    public async Task OnGetAsync_GroupsMultiMarketPredictionsForSameFixture_IntoSingleSettledFixtureGroup()
    {
        var matchDate = new DateOnly(2026, 10, 9);
        var predictions = new List<Prediction>
        {
            new()
            {
                Id = 1,
                HomeTeam = "FC Osaka",
                AwayTeam = "Zweigen Kanazawa",
                League = "Japan - J3 League",
                MatchLocalDate = matchDate,
                MatchLocalTime = new TimeOnly(11, 0),
                Time = "11:00",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualScore = "1:1",
                FixtureKey = "fixture-osaka-kanazawa",
                WasPublished = true,
                IsCurrentRevision = true
            },
            new()
            {
                Id = 2,
                HomeTeam = "FC Osaka",
                AwayTeam = "Zweigen Kanazawa",
                League = "Japan - J3 League",
                MatchLocalDate = matchDate,
                MatchLocalTime = new TimeOnly(11, 0),
                Time = "11:00",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ActualScore = "1:1",
                FixtureKey = "fixture-osaka-kanazawa",
                WasPublished = true,
                IsCurrentRevision = true
            },
            new()
            {
                Id = 3,
                HomeTeam = "FC Osaka",
                AwayTeam = "Zweigen Kanazawa",
                League = "Japan - J3 League",
                MatchLocalDate = matchDate,
                MatchLocalTime = new TimeOnly(11, 0),
                Time = "11:00",
                PredictionCategory = "Over2.5Goals",
                PredictedOutcome = "Over 2.5",
                ActualScore = "1:1",
                FixtureKey = "fixture-osaka-kanazawa",
                WasPublished = true,
                IsCurrentRevision = true
            },
            new()
            {
                Id = 4,
                HomeTeam = "Arsenal",
                AwayTeam = "Chelsea",
                League = "England - Premier League",
                MatchLocalDate = matchDate,
                MatchLocalTime = new TimeOnly(15, 0),
                Time = "15:00",
                PredictionCategory = "StraightWin",
                PredictedOutcome = "Home Win",
                ActualScore = "2:1",
                FixtureKey = "fixture-arsenal-chelsea",
                WasPublished = true,
                IsCurrentRevision = true
            }
        };

        var fakeQueries = new FakePredictionQueries(predictions);
        var model = new ResultsModel(fakeQueries);
        await model.OnGetAsync();

        Assert.Equal(4, model.TotalCount);
        Assert.Single(model.DayGroups);

        var day = model.DayGroups[0];
        Assert.Equal(4, day.TotalCount);
        Assert.Equal(2, day.Fixtures.Count); // Exactly 2 unique fixtures, no duplicated match rows!

        var osakaFixture = day.Fixtures.FirstOrDefault(f => f.HomeTeam == "FC Osaka");
        Assert.NotNull(osakaFixture);
        Assert.Equal("1:1", osakaFixture.ActualScore);
        Assert.Equal(3, osakaFixture.Picks.Count);
        Assert.Contains(osakaFixture.Picks, p => p.MarketLabel == "BTTS" && p.IsWin);
        Assert.Contains(osakaFixture.Picks, p => p.MarketLabel == "Draw" && p.IsWin);
        Assert.Contains(osakaFixture.Picks, p => p.MarketLabel == "Over 2.5" && !p.IsWin);

        var arsenalFixture = day.Fixtures.FirstOrDefault(f => f.HomeTeam == "Arsenal");
        Assert.NotNull(arsenalFixture);
        Assert.Single(arsenalFixture.Picks);
        Assert.Equal("Straight Win", arsenalFixture.Picks[0].MarketLabel);
        Assert.True(arsenalFixture.Picks[0].IsWin);
    }
}
