using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class PredictionScoreClassHelperTests
{
    [Theory]
    [InlineData("BothTeamsScore", "BTTS", "1-1", true)]
    [InlineData("BothTeamsScore", "BTTS", "2-1", true)]
    [InlineData("BothTeamsScore", "BTTS", "1-0", false)]
    [InlineData("BothTeamsScore", "BTTS", "0-0", false)]
    [InlineData("BothTeamsScore", "No BTTS", "1-1", true)]
    [InlineData("BothTeamsScore", "No BTTS", "1-0", false)]
    public void HasLiveOptionDecided_BothTeamsScore(string category, string outcome, string score, bool expected)
    {
        var prediction = new Prediction
        {
            IsLive = true,
            PredictionCategory = category,
            PredictedOutcome = outcome,
            ActualScore = score
        };

        var actual = PredictionScoreClassHelper.HasLiveOptionDecided(prediction);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Over2.5Goals", "Over 2.5", "2-1", true)]
    [InlineData("Over2.5Goals", "Over 2.5", "3-0", true)]
    [InlineData("Over2.5Goals", "Over 2.5", "1-1", false)]
    [InlineData("Over2.5Goals", "Over 2.5", "0-0", false)]
    [InlineData("Over2.5Goals", "Under 2.5", "2-1", true)]
    [InlineData("Over2.5Goals", "Under 2.5", "1-1", false)]
    public void HasLiveOptionDecided_Over25Goals(string category, string outcome, string score, bool expected)
    {
        var prediction = new Prediction
        {
            IsLive = true,
            PredictionCategory = category,
            PredictedOutcome = outcome,
            ActualScore = score
        };

        var actual = PredictionScoreClassHelper.HasLiveOptionDecided(prediction);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Under2.5Goals", "Under 2.5", "3-0", true)]
    [InlineData("Under2.5Goals", "Under 2.5", "2-1", true)]
    [InlineData("Under2.5Goals", "Under 2.5", "1-1", false)]
    [InlineData("Under2.5Goals", "Under 2.5", "1-0", false)]
    [InlineData("Under2.5Goals", "Under 2.5", "0-0", false)]
    public void HasLiveOptionDecided_Under25Goals(string category, string outcome, string score, bool expected)
    {
        var prediction = new Prediction
        {
            IsLive = true,
            PredictionCategory = category,
            PredictedOutcome = outcome,
            ActualScore = score
        };

        var actual = PredictionScoreClassHelper.HasLiveOptionDecided(prediction);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("StraightWin", "Home Win", "2-0")]
    [InlineData("StraightWin", "Away Win", "0-3")]
    [InlineData("Draw", "Draw", "1-1")]
    public void HasLiveOptionDecided_StraightWinAndDraw_NeverDecidedUntilFinished(string category, string outcome, string score)
    {
        var prediction = new Prediction
        {
            IsLive = true,
            PredictionCategory = category,
            PredictedOutcome = outcome,
            ActualScore = score
        };

        var actual = PredictionScoreClassHelper.HasLiveOptionDecided(prediction);

        Assert.False(actual);
    }

    [Fact]
    public void HasLiveOptionDecided_ReturnsFalse_WhenNotLiveOrNoScore()
    {
        var notLive = new Prediction
        {
            IsLive = false,
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ActualScore = "2-1"
        };
        var noScore = new Prediction
        {
            IsLive = true,
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ActualScore = ""
        };

        Assert.False(PredictionScoreClassHelper.HasLiveOptionDecided(notLive));
        Assert.False(PredictionScoreClassHelper.HasLiveOptionDecided(noScore));
    }
}
