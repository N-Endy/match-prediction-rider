using System.Globalization;
using System.Text.Json;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Statistics;

public static class SignalBreakdownParser
{
    private const double AlignmentThresholdPoints = 5.0;
    private const double BookmakerDivergenceThresholdPoints = 8.0;

    public static SignalBreakdownSnapshot? TryParse(
        string? featureContributionsJson,
        string predictionCategory,
        string predictedOutcome,
        double? modelProbability = null)
    {
        if (string.IsNullOrWhiteSpace(featureContributionsJson) || featureContributionsJson == "{}")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(featureContributionsJson);
            var root = document.RootElement;
            var signalKey = ResolveSignalKey(predictionCategory, predictedOutcome);
            if (signalKey is null)
            {
                return null;
            }

            var feed = ReadSectionProbability(root, "calculatorSignal", signalKey);
            var bookmaker = ReadSectionProbability(root, "bookmakerSignal", signalKey);
            var statistical = ReadSectionProbability(root, "statisticalSignal", signalKey);
            var ensemble = ReadSectionProbability(root, "modelOutputs", signalKey) ?? modelProbability;
            var machineLearning = root.TryGetProperty("mlSignal", out var mlElement) &&
                                  mlElement.ValueKind == JsonValueKind.Number &&
                                  mlElement.TryGetDouble(out var mlValue)
                ? mlValue
                : (double?)null;

            var signals = new List<double>();
            if (feed.HasValue)
            {
                signals.Add(feed.Value);
            }

            if (bookmaker.HasValue)
            {
                signals.Add(bookmaker.Value);
            }

            if (statistical.HasValue)
            {
                signals.Add(statistical.Value);
            }

            if (machineLearning.HasValue)
            {
                signals.Add(machineLearning.Value);
            }

            if (ensemble.HasValue)
            {
                signals.Add(ensemble.Value);
            }

            var spreadPoints = signals.Count >= 2
                ? (signals.Max() - signals.Min()) * 100.0
                : 0.0;

            var bookmakerGapPoints = bookmaker.HasValue && ensemble.HasValue
                ? (ensemble.Value - bookmaker.Value) * 100.0
                : 0.0;

            var allAlign = feed.HasValue &&
                           bookmaker.HasValue &&
                           statistical.HasValue &&
                           spreadPoints < AlignmentThresholdPoints;
            var divergesFromBookmaker = bookmaker.HasValue &&
                                        ensemble.HasValue &&
                                        bookmakerGapPoints > BookmakerDivergenceThresholdPoints;
            var thinHistory = !statistical.HasValue &&
                              root.TryGetProperty("statisticalSignalApplied", out var applied) &&
                              applied.ValueKind == JsonValueKind.False;

            return new SignalBreakdownSnapshot
            {
                ModelSignals = new ModelSignalSnapshot
                {
                    Feed = RoundProbability(feed),
                    Bookmaker = RoundProbability(bookmaker),
                    Statistical = RoundProbability(statistical),
                    MachineLearning = RoundProbability(machineLearning),
                    Ensemble = RoundProbability(ensemble)
                },
                SignalAgreement = new SignalAgreementSnapshot
                {
                    SignalSpreadPoints = Math.Round(spreadPoints, 1),
                    AllSignalsAlign = allAlign,
                    ModelDivergesFromBookmaker = divergesFromBookmaker,
                    ThinHistory = thinHistory,
                    Summary = BuildAgreementSummary(allAlign, divergesFromBookmaker, thinHistory, spreadPoints)
                }
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static object? ToPayloadObject(SignalBreakdownSnapshot? breakdown)
    {
        if (breakdown is null)
        {
            return null;
        }

        return new
        {
            feed = breakdown.ModelSignals.Feed,
            bookmaker = breakdown.ModelSignals.Bookmaker,
            statistical = breakdown.ModelSignals.Statistical,
            machineLearning = breakdown.ModelSignals.MachineLearning,
            ensemble = breakdown.ModelSignals.Ensemble
        };
    }

    public static object? ToAgreementPayloadObject(SignalBreakdownSnapshot? breakdown)
    {
        if (breakdown is null)
        {
            return null;
        }

        return new
        {
            breakdown.SignalAgreement.SignalSpreadPoints,
            breakdown.SignalAgreement.AllSignalsAlign,
            breakdown.SignalAgreement.ModelDivergesFromBookmaker,
            breakdown.SignalAgreement.ThinHistory,
            breakdown.SignalAgreement.Summary
        };
    }

    private static string? ResolveSignalKey(string predictionCategory, string predictedOutcome)
    {
        return predictionCategory switch
        {
            "BothTeamsScore" => "btts",
            "Over2.5Goals" => "over25",
            "Under2.5Goals" => "under25",
            "Draw" => "draw",
            "StraightWin" => predictedOutcome.Contains("Away", StringComparison.OrdinalIgnoreCase) ? "awayWin" : "homeWin",
            _ => null
        };
    }

    private static double? ReadSectionProbability(JsonElement root, string sectionName, string signalKey)
    {
        if (!root.TryGetProperty(sectionName, out var section) ||
            section.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            section.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!section.TryGetProperty(signalKey, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetDouble(out var probability) ? probability : null;
    }

    private static double? RoundProbability(double? value) =>
        value.HasValue ? Math.Round(value.Value * 100.0, 1) : null;

    private static string BuildAgreementSummary(
        bool allAlign,
        bool divergesFromBookmaker,
        bool thinHistory,
        double spreadPoints)
    {
        if (thinHistory)
        {
            return "Thin history";
        }

        if (allAlign)
        {
            return "All signals align";
        }

        if (divergesFromBookmaker)
        {
            return "Model diverges from bookmaker";
        }

        return spreadPoints >= 10.0
            ? "Mixed signal disagreement"
            : "Moderate signal spread";
    }
}
