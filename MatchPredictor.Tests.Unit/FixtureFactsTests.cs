using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class FixtureFactsTests
{
    [Fact]
    public void BuildFixtureFacts_WithFullData_PopulatesFactsCorrectly()
    {
        var prediction = new Prediction
        {
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            League = "Premier League",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "Yes",
            ConfidenceScore = 0.72m,
            CalibratorUsed = "Isotonic",
            MatchDateTime = new DateTime(2026, 10, 25, 16, 30, 0, DateTimeKind.Utc)
        };

        var facts = FixtureFacts.BuildFixtureFacts(prediction, 1.85m, 0.12m, "WWWDW", "LDWWL");

        Assert.NotEmpty(facts);
        Assert.Contains(facts, f => f.Label == "Fixture" && f.Value == "Arsenal vs Chelsea");
        Assert.Contains(facts, f => f.Label == "Bookmaker Odds" && f.Value == "1.85");
        Assert.Contains(facts, f => f.Label == "Model Confidence" && f.Value == "72.0%");
        Assert.Contains(facts, f => f.Label == "Model Edge" && f.Value == "12.0%");
    }

    [Fact]
    public void BuildFixtureFacts_WithMissingOddsAndForm_MarksUnknown()
    {
        var prediction = new Prediction
        {
            HomeTeam = "Real Madrid",
            AwayTeam = "Barcelona",
            League = "LaLiga",
            PredictionCategory = "Over2.5Goals",
            PredictedOutcome = "Over 2.5",
            ConfidenceScore = null,
            CalibratorUsed = "Raw"
        };

        var facts = FixtureFacts.BuildFixtureFacts(prediction, null, null, null, null);
        var missing = FixtureFacts.MissingLabels(facts);

        Assert.Contains("Bookmaker Odds", missing);
        Assert.Contains("Model Confidence", missing);
        Assert.Contains("Model Edge", missing);
        Assert.Contains("Home Recent Form", missing);
        Assert.Contains("Away Recent Form", missing);
    }

    [Fact]
    public void ToJson_ProducesValidJsonRepresentation()
    {
        var facts = new List<FixtureFact>
        {
            new("Fixture", "Liverpool vs Man City", "database"),
            new("Odds", "2.10", "live-feed")
        };

        var json = FixtureFacts.ToJson(facts);

        Assert.Contains("\"label\":\"Fixture\"", json);
        Assert.Contains("\"value\":\"Liverpool vs Man City\"", json);
        Assert.Contains("\"provenance\":\"database\"", json);
    }
}
