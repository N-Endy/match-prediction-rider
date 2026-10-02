using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class PredictionSelectionAvailabilityTests
{
    private readonly DateTime _utcNow = new(2026, 10, 2, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CanAddToSelections_ReturnsTrue_ForUpcomingMatch()
    {
        var prediction = new Prediction
        {
            IsLive = false,
            ActualScore = null,
            MatchDateTime = _utcNow.AddHours(2),
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS"
        };

        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.True(canSelect);
    }

    [Fact]
    public void CanAddToSelections_ReturnsFalse_WhenMatchHasActualScoreAndNotLive()
    {
        var prediction = new Prediction
        {
            IsLive = false,
            ActualScore = "2-1",
            MatchDateTime = _utcNow.AddHours(-1),
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS"
        };

        var isEnded = PredictionDisplayHelper.IsMatchEnded(prediction, _utcNow);
        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.True(isEnded);
        Assert.False(canSelect);
    }

    [Fact]
    public void CanAddToSelections_ReturnsFalse_WhenMatchKickoffWasMoreThan120MinutesAgoAndNotLive()
    {
        var prediction = new Prediction
        {
            IsLive = false,
            ActualScore = null,
            MatchDateTime = _utcNow.AddMinutes(-130),
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win"
        };

        var isEnded = PredictionDisplayHelper.IsMatchEnded(prediction, _utcNow);
        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.True(isEnded);
        Assert.False(canSelect);
    }

    [Fact]
    public void CanAddToSelections_ReturnsFalse_WhenLiveAndBttsAlreadyHit()
    {
        var prediction = new Prediction
        {
            IsLive = true,
            ActualScore = "1-1",
            MatchDateTime = _utcNow.AddMinutes(-45),
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS"
        };

        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.False(canSelect);
    }

    [Fact]
    public void CanAddToSelections_ReturnsTrue_WhenLiveAndBttsStillPending()
    {
        var prediction = new Prediction
        {
            IsLive = true,
            ActualScore = "1-0",
            MatchDateTime = _utcNow.AddMinutes(-45),
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS"
        };

        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.True(canSelect);
    }

    [Fact]
    public void CanAddToSelections_ReturnsFalse_WhenLiveAndOver25AlreadyHit()
    {
        var prediction = new Prediction
        {
            IsLive = true,
            ActualScore = "2-1",
            MatchDateTime = _utcNow.AddMinutes(-60),
            PredictionCategory = "Over2.5Goals",
            PredictedOutcome = "Over 2.5"
        };

        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.False(canSelect);
    }

    [Fact]
    public void CanAddToSelections_ReturnsFalse_WhenLiveAndUnder25Busted()
    {
        var prediction = new Prediction
        {
            IsLive = true,
            ActualScore = "3-0",
            MatchDateTime = _utcNow.AddMinutes(-60),
            PredictionCategory = "Under2.5Goals",
            PredictedOutcome = "Under 2.5"
        };

        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.False(canSelect);
    }

    [Fact]
    public void CanAddToSelections_ReturnsTrue_WhenLiveAndUnder25StillAlive()
    {
        var prediction = new Prediction
        {
            IsLive = true,
            ActualScore = "1-0",
            MatchDateTime = _utcNow.AddMinutes(-30),
            PredictionCategory = "Under2.5Goals",
            PredictedOutcome = "Under 2.5"
        };

        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.True(canSelect);
    }

    [Fact]
    public void CanAddToSelections_ReturnsTrue_WhenLiveAndStraightWinStillPlaying()
    {
        var prediction = new Prediction
        {
            IsLive = true,
            ActualScore = "2-0",
            MatchDateTime = _utcNow.AddMinutes(-30),
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win"
        };

        var canSelect = PredictionDisplayHelper.CanAddToSelections(prediction, _utcNow);

        Assert.True(canSelect);
    }
}
