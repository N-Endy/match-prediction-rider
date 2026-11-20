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

    [Fact]
    public void TryParse_ParsesDoubleChance_SynthesizingFromBase1X2Signals()
    {
        const string json = """
            {
              "market": "DoubleChance",
              "calculatorSignal": { "homeWin": 0.55, "draw": 0.30, "awayWin": 0.15 },
              "bookmakerSignal": { "homeWin": 0.53, "draw": 0.31, "awayWin": 0.16 },
              "statisticalSignal": { "homeWin": 0.56, "draw": 0.29, "awayWin": 0.15 },
              "modelOutputs": { "doubleChance1X": 0.85 },
              "statisticalSignalApplied": true
            }
            """;

        var breakdown = SignalBreakdownParser.TryParse(json, "DoubleChance", "1X", 0.85);

        Assert.NotNull(breakdown);
        Assert.Equal(85.0, breakdown!.ModelSignals.Feed); // 0.55 + 0.30 = 0.85 -> 85.0%
        Assert.Equal(84.0, breakdown.ModelSignals.Bookmaker); // 0.53 + 0.31 = 0.84 -> 84.0%
        Assert.Equal(85.0, breakdown.ModelSignals.Statistical); // 0.56 + 0.29 = 0.85 -> 85.0%
        Assert.Equal(85.0, breakdown.ModelSignals.Ensemble);
        Assert.True(breakdown.SignalAgreement.AllSignalsAlign);
        Assert.True(breakdown.SignalAgreement.SignalSpreadPoints < 5.0);
    }

    [Fact]
    public void TryParse_ParsesOver15Goals_WhenExplicitSignalPresent()
    {
        const string json = """
            {
              "market": "Over15Goals",
              "calculatorSignal": { "over15": 0.88 },
              "bookmakerSignal": { "over15": 0.86 },
              "statisticalSignal": { "over15": 0.87 },
              "modelOutputs": { "over15": 0.88 },
              "statisticalSignalApplied": true
            }
            """;

        var breakdown = SignalBreakdownParser.TryParse(json, "Over1.5Goals", "Over 1.5", 0.88);

        Assert.NotNull(breakdown);
        Assert.Equal(88.0, breakdown!.ModelSignals.Feed);
        Assert.Equal(86.0, breakdown.ModelSignals.Bookmaker);
        Assert.Equal(87.0, breakdown.ModelSignals.Statistical);
        Assert.True(breakdown.SignalAgreement.AllSignalsAlign);
        Assert.True(breakdown.SignalAgreement.SignalSpreadPoints < 5.0);
    }
}
