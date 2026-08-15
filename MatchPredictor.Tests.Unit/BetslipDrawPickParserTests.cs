using MatchPredictor.Domain.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class BetslipDrawPickParserTests
{
    [Fact]
    public void ParsePredictionIds_ReadsValidJsonPicks()
    {
        const string json = """
            {"picks":[{"predictionId":11,"reason":"Strong draw signals"},{"predictionId":22,"reason":"Balanced xG"}]}
            """;

        var ids = BetslipDrawPickParser.ParsePredictionIds(json, 5);
        Assert.Equal([11, 22], ids);

        var reasons = BetslipDrawPickParser.ParseReasons(json);
        Assert.Equal("Strong draw signals", reasons[11]);
    }

    [Fact]
    public void ParsePredictionIds_ReturnsEmpty_OnJunk()
    {
        var ids = BetslipDrawPickParser.ParsePredictionIds("not-json-at-all", 5);
        Assert.Empty(ids);
    }

    [Fact]
    public void ParsePredictionIds_SalvagesIdsFromTruncatedPayload()
    {
        const string truncated = """{"picks":[{"predictionId":7,"reason":"ok"},{"predictionId":9,"reason":"cut""";
        var ids = BetslipDrawPickParser.ParsePredictionIds(truncated, 5);
        Assert.Contains(7, ids);
        Assert.Contains(9, ids);
    }

    [Fact]
    public void ParseRiskNote_ReadsRiskNoteFromPayload()
    {
        const string json = """
            {"picks":[{"predictionId":11,"reason":"Strong home"}],"riskNote":"Heavy stake — verify XI at kickoff."}
            """;

        var note = BetslipDrawPickParser.ParseRiskNote(json);
        Assert.Equal("Heavy stake — verify XI at kickoff.", note);
    }

    [Fact]
    public void ParseRiskNote_ReturnsNull_OnJunk()
    {
        Assert.Null(BetslipDrawPickParser.ParseRiskNote("not-json"));
    }

    [Fact]
    public void ParseOrderedPredictionIds_ReadsRankedArray()
    {
        const string json = """{"orderedPredictionIds":[30,10,20]}""";
        var ids = BetslipDrawPickParser.ParseOrderedPredictionIds(json);
        Assert.Equal([30, 10, 20], ids);
    }

    [Fact]
    public void ParseOrderedPredictionIds_FallsBackToPicksArray()
    {
        const string json = """{"picks":[{"predictionId":8},{"predictionId":4}]}""";
        var ids = BetslipDrawPickParser.ParseOrderedPredictionIds(json);
        Assert.Equal([8, 4], ids);
    }
}
