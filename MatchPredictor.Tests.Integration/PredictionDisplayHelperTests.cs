using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class PredictionDisplayHelperTests
{
    [Theory]
    [InlineData("BothTeamsScore", "BTTS", "2:4")]
    [InlineData("Over2.5Goals", "Over 2.5", "2:2")]
    [InlineData("StraightWin", "Home Win", "5:1")]
    [InlineData("Draw", "Draw", "1:1")]
    public void IsPredictionCorrect_UsesActualScoreWhenStoredOutcomeIsMissing(
        string predictionCategory,
        string predictedOutcome,
        string actualScore)
    {
        var prediction = CreatePrediction(predictionCategory, predictedOutcome, actualScore);

        var isCorrect = PredictionDisplayHelper.IsPredictionCorrect(prediction);

        Assert.True(isCorrect);
    }

    [Theory]
    [InlineData("BothTeamsScore", "BTTS", "2:0")]
    [InlineData("Over2.5Goals", "Over 2.5", "1:1")]
    [InlineData("StraightWin", "Home Win", "1:3")]
    [InlineData("Draw", "Draw", "3:1")]
    public void IsPredictionCorrect_ReturnsFalseWhenScoreDoesNotSupportPrediction(
        string predictionCategory,
        string predictedOutcome,
        string actualScore)
    {
        var prediction = CreatePrediction(predictionCategory, predictedOutcome, actualScore);

        var isCorrect = PredictionDisplayHelper.IsPredictionCorrect(prediction);

        Assert.False(isCorrect);
    }

    [Fact]
    public void IsLivePredictionCorrect_DoesNotTreatStraightWinLeaderAsSettledCorrect()
    {
        var prediction = CreatePrediction("StraightWin", "Home Win", "1:0");
        prediction.IsLive = true;

        var isCorrect = PredictionDisplayHelper.IsLivePredictionCorrect(prediction);

        Assert.False(isCorrect);
    }

    [Fact]
    public void IsLivePredictionCorrect_AllowsLiveBttsWhenConditionAlreadyMet()
    {
        var prediction = CreatePrediction("BothTeamsScore", "BTTS", "1:1");
        prediction.IsLive = true;

        var isCorrect = PredictionDisplayHelper.IsLivePredictionCorrect(prediction);

        Assert.True(isCorrect);
    }

    private static Prediction CreatePrediction(string predictionCategory, string predictedOutcome, string actualScore)
    {
        return new Prediction
        {
            PredictionCategory = predictionCategory,
            PredictedOutcome = predictedOutcome,
            ActualScore = actualScore,
            ActualOutcome = null
        };
    }
}
