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

    [Fact]
    public void ParseScreenedPassers_ReadsPassedArrayAndDropsDuplicates()
    {
        const string json = """
            {"passed":[{"predictionId":11,"score":88,"reason":"Form fits"},{"predictionId":11,"score":10,"reason":"dup"},{"predictionId":22,"score":70}]}
            """;

        var passed = BetslipDrawPickParser.ParseScreenedPassers(json);
        Assert.Equal(2, passed.Count);
        Assert.Equal(11, passed[0].PredictionId);
        Assert.Equal(88, passed[0].Score);
        Assert.Equal("Form fits", passed[0].Reason);
        Assert.Equal(22, passed[1].PredictionId);
    }

    [Fact]
    public void IsExplicitEmptyPassedList_TrueForEmptyPassedArray()
    {
        Assert.True(BetslipDrawPickParser.IsExplicitEmptyPassedList("""{"passed":[]}"""));
        Assert.False(BetslipDrawPickParser.IsExplicitEmptyPassedList("not-json"));
        Assert.False(BetslipDrawPickParser.IsExplicitEmptyPassedList("""{"picks":[]}"""));
    }

    [Fact]
    public void ParseLadderComposeSlips_ReadsSlipNumbersAndIds()
    {
        const string json = """
            {"slips":[{"slipNumber":1,"predictionIds":[10,11]},{"slipNumber":2,"picks":[{"predictionId":20}]}]}
            """;

        var slips = BetslipDrawPickParser.ParseLadderComposeSlips(json);
        Assert.Equal(2, slips.Count);
        Assert.Equal(1, slips[0].SlipNumber);
        Assert.Equal([10, 11], slips[0].PredictionIds);
        Assert.Equal(2, slips[1].SlipNumber);
        Assert.Equal([20], slips[1].PredictionIds);
    }
}
