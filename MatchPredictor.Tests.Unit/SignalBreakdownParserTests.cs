using MatchPredictor.Infrastructure.Statistics;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class SignalBreakdownParserTests
{
    [Fact]
    public void TryParse_ReturnsAgreementSummary_WhenSignalsDisagree()
    {
        const string json = """
            {
              "market": "BothTeamsScore",
              "calculatorSignal": { "btts": 0.62 },
              "bookmakerSignal": { "btts": 0.48 },
              "statisticalSignal": { "btts": 0.55 },
              "modelOutputs": { "btts": 0.66 },
              "statisticalSignalApplied": true
            }
            """;

        var breakdown = SignalBreakdownParser.TryParse(json, "BothTeamsScore", "BTTS", 0.66);

        Assert.NotNull(breakdown);
        Assert.Equal(62.0, breakdown!.ModelSignals.Feed);
        Assert.Equal(48.0, breakdown.ModelSignals.Bookmaker);
        Assert.True(breakdown.SignalAgreement.ModelDivergesFromBookmaker);
        Assert.False(breakdown.SignalAgreement.ThinHistory);
    }

    [Fact]
    public void TryParse_FlagsThinHistory_WhenStatisticalSignalMissing()
    {
        const string json = """
            {
              "market": "Over25Goals",
              "calculatorSignal": { "over25": 0.58 },
              "bookmakerSignal": { "over25": 0.54 },
              "modelOutputs": { "over25": 0.60 },
              "statisticalSignalApplied": false
            }
            """;

        var breakdown = SignalBreakdownParser.TryParse(json, "Over2.5Goals", "Over 2.5", 0.60);

        Assert.NotNull(breakdown);
        Assert.True(breakdown!.SignalAgreement.ThinHistory);
        Assert.Equal("Thin history", breakdown.SignalAgreement.Summary);
    }
}
