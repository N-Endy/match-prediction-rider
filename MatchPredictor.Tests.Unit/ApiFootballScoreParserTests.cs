using System.Text.Json;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class ApiFootballScoreParserTests
{
    [Fact]
    public void TryReadRegularTimeScore_ReadsFullTimeWhenExtraTimeGoalsArePresent()
    {
        using var document = JsonDocument.Parse("""
            {
              "goals": { "home": 1, "away": 0 },
              "score": {
                "halftime": { "home": 0, "away": 0 },
                "fulltime": { "home": 0, "away": 0 },
                "extratime": { "home": 1, "away": 0 },
                "penalty": { "home": null, "away": null }
              }
            }
            """);

        var regularTimeScore = ApiFootballScoreParser.TryReadRegularTimeScore(document.RootElement);

        Assert.Equal("0:0", regularTimeScore);
    }

    [Fact]
    public void TryReadRegularTimeScore_ReturnsNullWhenFullTimeIsMissing()
    {
        using var document = JsonDocument.Parse("""
            {
              "goals": { "home": 1, "away": 0 },
              "score": {
                "fulltime": { "home": null, "away": null }
              }
            }
            """);

        Assert.Null(ApiFootballScoreParser.TryReadRegularTimeScore(document.RootElement));
    }
}
