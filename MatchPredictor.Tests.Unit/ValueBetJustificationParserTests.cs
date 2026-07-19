using MatchPredictor.Application.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class ValueBetJustificationParserTests
{
    [Fact]
    public void Parse_ReadsWrappedPicksObject()
    {
        var json = """
            {
              "picks": [
                { "CandidateKey": "a|b|BTTS", "AiJustification": "Model 80% vs market 60% (+20pp edge)." }
              ]
            }
            """;

        var result = ValueBetJustificationParser.Parse(json);

        Assert.Equal("Model 80% vs market 60% (+20pp edge).", result["a|b|BTTS"]);
    }

    [Fact]
    public void Parse_StripsMarkdownFences()
    {
        var json = """
            ```json
            {
              "picks": [
                { "CandidateKey": "key-1", "AiJustification": "Short note." }
              ]
            }
            ```
            """;

        var result = ValueBetJustificationParser.Parse(json);

        Assert.Equal("Short note.", result["key-1"]);
    }

    [Fact]
    public void Parse_SalvagesCompletePicks_FromTruncatedPayload()
    {
        var json = """
            {
              "picks": [
                { "CandidateKey": "complete-1", "AiJustification": "First pick is complete." },
                { "CandidateKey": "complete-2", "AiJustification": "Second pick is also complete." },
                { "CandidateKey": "truncated", "AiJustification": "This one was cut off mid-senten
            """;

        var result = ValueBetJustificationParser.Parse(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("First pick is complete.", result["complete-1"]);
        Assert.Equal("Second pick is also complete.", result["complete-2"]);
        Assert.False(result.ContainsKey("truncated"));
    }
}
