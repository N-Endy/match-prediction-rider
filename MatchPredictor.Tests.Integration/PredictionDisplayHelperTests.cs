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

    [Fact]
    public void GetScoreClass_KeepsLiveUnder25NeutralUntilItIsBusted()
    {
        var prediction = CreatePrediction("Under2.5Goals", "Under 2.5", "1:1");
        prediction.IsLive = true;
        prediction.MatchDateTime = DateTime.UtcNow.AddMinutes(-20);

        var scoreClass = PredictionDisplayHelper.GetScoreClass(prediction, DateTime.UtcNow);

        Assert.Equal("mp-score-live", scoreClass);
    }

    [Fact]
    public void GetScoreClass_MarksLiveUnder25RedWhenThirdGoalArrives()
    {
        var prediction = CreatePrediction("Under2.5Goals", "Under 2.5", "2:1");
        prediction.IsLive = true;
        prediction.MatchDateTime = DateTime.UtcNow.AddMinutes(-35);

        var scoreClass = PredictionDisplayHelper.GetScoreClass(prediction, DateTime.UtcNow);

        Assert.Equal("mp-score-incorrect", scoreClass);
    }

    [Fact]
    public void GetScoreClass_MarksFinishedUnder25GreenWhenItLands()
    {
        var prediction = CreatePrediction("Under2.5Goals", "Under 2.5", "1:1");
        prediction.IsLive = false;

        var scoreClass = PredictionDisplayHelper.GetScoreClass(prediction, DateTime.UtcNow);

        Assert.Equal("mp-score-correct", scoreClass);
    }

    [Fact]
    public void GenerateMatchSlug_CleansPunctuationAndFormatsProperly()
    {
        var slug = PredictionDisplayHelper.GenerateMatchSlug("Arsenal F.C.", "Chelsea (London)");
        Assert.Equal("arsenal-f-c-vs-chelsea-london", slug);
    }

    [Fact]
    public void GetMatchUrl_CreatesCanonicalPathWithIdAndSlug()
    {
        var prediction = new Prediction
        {
            Id = 42,
            HomeTeam = "Real Madrid",
            AwayTeam = "Barcelona"
        };

        var url = PredictionDisplayHelper.GetMatchUrl(prediction);
        Assert.Equal("/match/42/real-madrid-vs-barcelona", url);
    }

    [Fact]
    public void GetPredictionBadgeClass_FormatsBttsNoCorrectly()
    {
        var bttsNoPrediction = new Prediction
        {
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "No BTTS"
        };

        var bttsYesPrediction = new Prediction
        {
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS"
        };

        Assert.Equal("mp-badge-btts-no", PredictionDisplayHelper.GetPredictionBadgeClass(bttsNoPrediction));
        Assert.Equal("No BTTS", PredictionDisplayHelper.GetPredictionBadgeText(bttsNoPrediction));

        Assert.Equal("mp-badge-btts", PredictionDisplayHelper.GetPredictionBadgeClass(bttsYesPrediction));
        Assert.Equal("BTTS", PredictionDisplayHelper.GetPredictionBadgeText(bttsYesPrediction));
    }

    [Fact]
    public void GetPredictionBadgeClass_ProvidesExpectedBadges_ForOver15AndDoubleChance_WithoutPurple()
    {
        var over15 = new Prediction { PredictionCategory = "Over1.5Goals", PredictedOutcome = "Over 1.5" };
        var dc = new Prediction { PredictionCategory = "DoubleChance", PredictedOutcome = "1X" };
        var bttsNo = new Prediction { PredictionCategory = "BothTeamsScore", PredictedOutcome = "No BTTS" };

        var over15Badge = PredictionDisplayHelper.GetPredictionBadgeClass(over15);
        var dcBadge = PredictionDisplayHelper.GetPredictionBadgeClass(dc);
        var bttsNoBadge = PredictionDisplayHelper.GetPredictionBadgeClass(bttsNo);

        Assert.Equal("mp-badge-over15", over15Badge);
        Assert.Equal("mp-badge-double-chance", dcBadge);
        Assert.Equal("mp-badge-btts-no", bttsNoBadge);

        // Verification of Purple Ban compliance: badge classes do not contain purple/violet
        Assert.DoesNotContain("purple", over15Badge);
        Assert.DoesNotContain("violet", over15Badge);
        Assert.DoesNotContain("purple", dcBadge);
        Assert.DoesNotContain("violet", dcBadge);
        Assert.DoesNotContain("purple", bttsNoBadge);
        Assert.DoesNotContain("violet", bttsNoBadge);
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
