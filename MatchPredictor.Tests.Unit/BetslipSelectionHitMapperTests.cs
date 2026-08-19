using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class BetslipSelectionHitMapperTests
{
    [Fact]
    public void Map_ReturnsPending_WhenPredictionIsMissing()
    {
        var status = BetslipSelectionHitMapper.Map(null, DateTime.UtcNow);

        Assert.Equal(BetslipSelectionHitStatus.Pending, status);
    }

    [Fact]
    public void Map_ReturnsPending_WhenUnsettledAndNotLive()
    {
        var prediction = CreatePrediction("StraightWin", "Home Win");

        var status = BetslipSelectionHitMapper.Map(prediction, DateTime.UtcNow);

        Assert.Equal(BetslipSelectionHitStatus.Pending, status);
    }

    [Fact]
    public void Map_ReturnsWon_WhenSettledAndCorrect()
    {
        var prediction = CreatePrediction("StraightWin", "Home Win", actualScore: "2-1", actualOutcome: "Home Win");

        var status = BetslipSelectionHitMapper.Map(prediction, DateTime.UtcNow);

        Assert.Equal(BetslipSelectionHitStatus.Won, status);
    }

    [Fact]
    public void Map_ReturnsLost_WhenSettledAndIncorrect()
    {
        var prediction = CreatePrediction("StraightWin", "Home Win", actualScore: "0-1", actualOutcome: "Away Win");

        var status = BetslipSelectionHitMapper.Map(prediction, DateTime.UtcNow);

        Assert.Equal(BetslipSelectionHitStatus.Lost, status);
    }

    [Fact]
    public void Map_ReturnsLive_WhenMatchIsInPlayAndNotDecided()
    {
        var now = DateTime.UtcNow;
        var prediction = CreatePrediction("StraightWin", "Home Win", actualScore: "0-0");
        prediction.IsLive = true;
        prediction.MatchDateTime = now.AddMinutes(-20);

        var status = BetslipSelectionHitMapper.Map(prediction, now);

        Assert.Equal(BetslipSelectionHitStatus.Live, status);
    }

    [Fact]
    public void MapSelection_FallsBackToFixtureMatch_WhenPredictionIdIsMissing()
    {
        var date = new DateOnly(2026, 8, 17);
        var selection = new BetslipSelection
        {
            HomeTeam = "Hacken",
            AwayTeam = "Halmstads",
            PredictedOutcome = "Home Win"
        };
        var prediction = CreatePrediction("StraightWin", "Home Win", actualScore: "2-0", actualOutcome: "Home Win");
        prediction.MatchLocalDate = date;
        prediction.HomeTeam = "Hacken";
        prediction.AwayTeam = "Halmstads";

        var status = BetslipSelectionHitMapper.MapSelection(
            selection,
            date,
            new Dictionary<int, Prediction>(),
            [prediction],
            DateTime.UtcNow);

        Assert.Equal(BetslipSelectionHitStatus.Won, status);
    }

    [Fact]
    public void MapSelection_UsesSettledCurrentRevision_WhenStoredPredictionIdIsUnsettled()
    {
        var date = new DateOnly(2026, 8, 18);
        var stored = CreatePrediction("BothTeamsScore", "BTTS");
        stored.Id = 123;
        stored.IsCurrentRevision = false;
        stored.MatchLocalDate = date;
        stored.HomeTeam = "Cardiff City";
        stored.AwayTeam = "Barnsley";

        var current = CreatePrediction("BothTeamsScore", "BTTS", actualScore: "2-1", actualOutcome: "BTTS");
        current.Id = 456;
        current.IsCurrentRevision = true;
        current.MatchLocalDate = date;
        current.HomeTeam = "Cardiff City";
        current.AwayTeam = "Barnsley";

        var selection = new BetslipSelection
        {
            PredictionId = 123,
            HomeTeam = "Cardiff City",
            AwayTeam = "Barnsley",
            PredictedOutcome = "BTTS"
        };

        var status = BetslipSelectionHitMapper.MapSelection(
            selection,
            date,
            new Dictionary<int, Prediction> { [123] = stored },
            [current],
            DateTime.UtcNow);

        Assert.Equal(BetslipSelectionHitStatus.Won, status);
    }

    [Fact]
    public void MapSelection_UsesSettledCurrentRevision_WhenStoredPredictionIdIsIncorrect()
    {
        var date = new DateOnly(2026, 8, 18);
        var stored = CreatePrediction("BothTeamsScore", "BTTS");
        stored.Id = 10;
        stored.MatchLocalDate = date;
        stored.HomeTeam = "Home";
        stored.AwayTeam = "Away";

        var current = CreatePrediction("BothTeamsScore", "BTTS", actualScore: "1-0", actualOutcome: "Home Win");
        current.Id = 11;
        current.MatchLocalDate = date;
        current.HomeTeam = "Home";
        current.AwayTeam = "Away";

        var selection = new BetslipSelection
        {
            PredictionId = 10,
            HomeTeam = "Home",
            AwayTeam = "Away",
            PredictedOutcome = "BTTS"
        };

        var status = BetslipSelectionHitMapper.MapSelection(
            selection,
            date,
            new Dictionary<int, Prediction> { [10] = stored },
            [current],
            DateTime.UtcNow);

        Assert.Equal(BetslipSelectionHitStatus.Lost, status);
    }

    [Fact]
    public void MapSelection_UsesStoredRow_WhenItIsAlreadySettled()
    {
        var date = new DateOnly(2026, 8, 18);
        var selection = new BetslipSelection
        {
            PredictionId = 11,
            HomeTeam = "Home",
            AwayTeam = "Away",
            PredictedOutcome = "Home Win"
        };
        var stored = CreatePrediction("StraightWin", "Home Win", actualScore: "0-1", actualOutcome: "Away Win");
        stored.Id = 11;
        stored.MatchLocalDate = date;

        var current = CreatePrediction("StraightWin", "Home Win", actualScore: "2-0", actualOutcome: "Home Win");
        current.Id = 22;
        current.MatchLocalDate = date;

        var status = BetslipSelectionHitMapper.MapSelection(
            selection,
            date,
            new Dictionary<int, Prediction> { [11] = stored },
            [current],
            DateTime.UtcNow);

        Assert.Equal(BetslipSelectionHitStatus.Lost, status);
    }

    private static Prediction CreatePrediction(
        string category,
        string predictedOutcome,
        string? actualScore = null,
        string? actualOutcome = null)
    {
        return new Prediction
        {
            Date = "18-08-2026",
            Time = "15:00",
            MatchLocalDate = new DateOnly(2026, 8, 18),
            League = "Test League",
            HomeTeam = "Home",
            AwayTeam = "Away",
            PredictionCategory = category,
            PredictedOutcome = predictedOutcome,
            ActualScore = actualScore,
            ActualOutcome = actualOutcome
        };
    }
}
