using System.Text.Json;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Services;

public sealed record FixtureFact(string Label, string Value, string Provenance, decimal? NumericValue = null);

public static class FixtureFacts
{
    public const string SystemPrompt =
        """
        You are MatchPredictor Football Advisor. Answer only from the facts JSON provided in the user prompt.
        If any statistic, lineup news, historical head-to-head record, or odds quote is missing or labeled unknown, state UNKNOWN.
        Do not invent team scores, player injuries, mathematical edge, or bookmaker lines under any circumstances.
        Use plain, objective language. Keep the response to 1-2 concise paragraphs with bullet points for key takeaways.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string ToJson(IReadOnlyList<FixtureFact> facts) =>
        JsonSerializer.Serialize(facts.Select(f => new
        {
            label = f.Label,
            value = f.Value,
            provenance = f.Provenance
        }), JsonOptions);

    public static IReadOnlyList<string> MissingLabels(IReadOnlyList<FixtureFact> facts) =>
        facts
            .Where(f => string.Equals(f.Value, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Provenance, "unknown", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Label)
            .ToList();

    public static List<FixtureFact> BuildFixtureFacts(
        Prediction prediction,
        decimal? currentOdds = null,
        decimal? valueEdge = null,
        string? homeForm = null,
        string? awayForm = null)
    {
        var kickoffTime = prediction.MatchDateTime?.ToString("yyyy-MM-dd HH:mm UTC") 
                          ?? (!string.IsNullOrWhiteSpace(prediction.Time) ? prediction.Time : "UNKNOWN");

        var facts = new List<FixtureFact>
        {
            new("Fixture", $"{prediction.HomeTeam} vs {prediction.AwayTeam}", "database"),
            new("League", prediction.League, "database"),
            new("Kickoff (UTC)", kickoffTime, kickoffTime == "UNKNOWN" ? "unknown" : "database"),
            new("Predicted Market", prediction.PredictionCategory, "model"),
            new("Recommended Outcome", prediction.PredictedOutcome, "model"),
            new("Model Confidence", prediction.ConfidenceScore.HasValue ? $"{prediction.ConfidenceScore.Value * 100:F1}%" : "UNKNOWN", prediction.ConfidenceScore.HasValue ? "calibrated-model" : "unknown"),
            new("Calibrator", prediction.CalibratorUsed, "model-metadata"),
            new("Bookmaker Odds", currentOdds.HasValue ? currentOdds.Value.ToString("F2") : "UNKNOWN", currentOdds.HasValue ? "live-odds-snapshot" : "unknown"),
            new("Model Edge", valueEdge.HasValue ? $"{valueEdge.Value * 100:F1}%" : "UNKNOWN", valueEdge.HasValue ? "ev-engine" : "unknown"),
            new("Home Recent Form", string.IsNullOrWhiteSpace(homeForm) ? "UNKNOWN" : homeForm, string.IsNullOrWhiteSpace(homeForm) ? "unknown" : "team-stats"),
            new("Away Recent Form", string.IsNullOrWhiteSpace(awayForm) ? "UNKNOWN" : awayForm, string.IsNullOrWhiteSpace(awayForm) ? "unknown" : "team-stats")
        };

        return facts;
    }
}
