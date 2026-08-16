using System.Text.Json;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Helpers;

public static partial class BetslipDrawPickParser
{
    public static IReadOnlyList<int> ParsePredictionIds(string aiResponseJson, int maxCount)
    {
        if (maxCount <= 0 || string.IsNullOrWhiteSpace(aiResponseJson))
        {
            return [];
        }

        var normalized = NormalizeAiJson(aiResponseJson);
        if (TryParseIds(normalized, out var ids) && ids.Count > 0)
        {
            return ids.Distinct().Take(maxCount).ToList();
        }

        if (TrySalvageIds(normalized, out var salvaged) && salvaged.Count > 0)
        {
            return salvaged.Distinct().Take(maxCount).ToList();
        }

        return [];
    }

    public static Dictionary<int, string> ParseReasons(string aiResponseJson)
    {
        var reasons = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(aiResponseJson))
        {
            return reasons;
        }

        var normalized = NormalizeAiJson(aiResponseJson);
        try
        {
            using var document = JsonDocument.Parse(normalized);
            foreach (var element in EnumeratePickElements(document.RootElement))
            {
                if (!TryReadPredictionId(element, out var predictionId))
                {
                    continue;
                }

                var reason = ReadReason(element);
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    reasons[predictionId] = reason.Trim();
                }
            }
        }
        catch (JsonException)
        {
            // Ignore — caller falls back to confidence ranking.
        }

        return reasons;
    }

    public static string? ParseRiskNote(string aiResponseJson)
    {
        if (string.IsNullOrWhiteSpace(aiResponseJson))
        {
            return null;
        }

        var normalized = NormalizeAiJson(aiResponseJson);
        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var propertyName in new[] { "riskNote", "RiskNote", "risk", "Risk", "summary" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var noteElement) &&
                    noteElement.ValueKind == JsonValueKind.String)
                {
                    var note = noteElement.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        return note;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore — caller falls back without a risk note.
        }

        return null;
    }

    public static IReadOnlyList<int> ParseOrderedPredictionIds(string aiResponseJson)
    {
        if (string.IsNullOrWhiteSpace(aiResponseJson))
        {
            return [];
        }

        var normalized = NormalizeAiJson(aiResponseJson);
        if (TryParseOrderedIds(normalized, out var ordered) && ordered.Count > 0)
        {
            return ordered.Distinct().ToList();
        }

        return ParsePredictionIds(aiResponseJson, 200);
    }

    public static IReadOnlyList<BetslipScreenPick> ParseScreenedPassers(string aiResponseJson)
    {
        if (string.IsNullOrWhiteSpace(aiResponseJson))
        {
            return [];
        }

        var normalized = NormalizeAiJson(aiResponseJson);
        try
        {
            using var document = JsonDocument.Parse(normalized);
            var passed = new List<BetslipScreenPick>();
            foreach (var element in EnumerateNamedArray(document.RootElement, "passed", "Passed", "picks", "selections"))
            {
                if (!TryReadPredictionId(element, out var predictionId))
                {
                    continue;
                }

                var score = 50d;
                foreach (var propertyName in new[] { "score", "Score", "rating", "Rating" })
                {
                    if (element.TryGetProperty(propertyName, out var scoreElement) &&
                        scoreElement.ValueKind == JsonValueKind.Number &&
                        scoreElement.TryGetDouble(out var parsed))
                    {
                        score = parsed;
                        break;
                    }
                }

                passed.Add(new BetslipScreenPick
                {
                    PredictionId = predictionId,
                    Score = Math.Clamp(score, 0d, 100d),
                    Reason = ReadReason(element)
                });
            }

            return passed
                .GroupBy(pick => pick.PredictionId)
                .Select(group => group.First())
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool IsExplicitEmptyPassedList(string aiResponseJson)
    {
        if (string.IsNullOrWhiteSpace(aiResponseJson))
        {
            return false;
        }

        var normalized = NormalizeAiJson(aiResponseJson);
        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var propertyName in new[] { "passed", "Passed" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var passed) &&
                    passed.ValueKind == JsonValueKind.Array)
                {
                    return passed.GetArrayLength() == 0;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<LadderComposeSlip> ParseLadderComposeSlips(string aiResponseJson)
    {
        if (string.IsNullOrWhiteSpace(aiResponseJson))
        {
            return [];
        }

        var normalized = NormalizeAiJson(aiResponseJson);
        try
        {
            using var document = JsonDocument.Parse(normalized);
            var slips = new List<LadderComposeSlip>();
            foreach (var element in EnumerateNamedArray(document.RootElement, "slips", "Slips"))
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var slipNumber = 0;
                foreach (var propertyName in new[] { "slipNumber", "SlipNumber", "number", "Number" })
                {
                    if (element.TryGetProperty(propertyName, out var numberElement) &&
                        numberElement.ValueKind == JsonValueKind.Number &&
                        numberElement.TryGetInt32(out var parsed) &&
                        parsed > 0)
                    {
                        slipNumber = parsed;
                        break;
                    }
                }

                if (slipNumber <= 0)
                {
                    continue;
                }

                var ids = new List<int>();
                foreach (var propertyName in new[] { "predictionIds", "PredictionIds", "picks", "Picks" })
                {
                    if (!element.TryGetProperty(propertyName, out var picks) ||
                        picks.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var pick in picks.EnumerateArray())
                    {
                        if (pick.ValueKind == JsonValueKind.Number &&
                            pick.TryGetInt32(out var id) &&
                            id > 0)
                        {
                            ids.Add(id);
                            continue;
                        }

                        if (TryReadPredictionId(pick, out var predictionId))
                        {
                            ids.Add(predictionId);
                        }
                    }

                    break;
                }

                slips.Add(new LadderComposeSlip
                {
                    SlipNumber = slipNumber,
                    PredictionIds = ids.Distinct().ToList()
                });
            }

            return slips;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeAiJson(string aiResponseJson)
    {
        var trimmed = aiResponseJson.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                trimmed = trimmed[..fenceEnd];
            }

            trimmed = trimmed.Trim();
        }

        var objectStart = trimmed.IndexOf('{');
        var arrayStart = trimmed.IndexOf('[');
        if (objectStart < 0 && arrayStart < 0)
        {
            return trimmed;
        }

        if (objectStart >= 0 && (arrayStart < 0 || objectStart < arrayStart))
        {
            return trimmed[objectStart..].Trim();
        }

        return trimmed[arrayStart..].Trim();
    }

    private static bool TryParseIds(string json, out List<int> ids)
    {
        ids = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var element in EnumeratePickElements(document.RootElement))
            {
                if (TryReadPredictionId(element, out var predictionId))
                {
                    ids.Add(predictionId);
                }
            }

            return ids.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseOrderedIds(string json, out List<int> ids)
    {
        ids = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var propertyName in new[] { "orderedPredictionIds", "OrderedPredictionIds", "rankedPredictionIds", "RankedPredictionIds" })
            {
                if (!document.RootElement.TryGetProperty(propertyName, out var ranked) ||
                    ranked.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in ranked.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Number &&
                        element.TryGetInt32(out var id) &&
                        id > 0)
                    {
                        ids.Add(id);
                        continue;
                    }

                    if (TryReadPredictionId(element, out var predictionId))
                    {
                        ids.Add(predictionId);
                    }
                }

                return ids.Count > 0;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TrySalvageIds(string json, out List<int> ids)
    {
        ids = [];
        foreach (Match match in PredictionIdRegex().Matches(json))
        {
            if (int.TryParse(match.Groups[1].Value, out var id) && id > 0)
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0;
    }

    private static IEnumerable<JsonElement> EnumeratePickElements(JsonElement root) =>
        EnumerateNamedArray(root, "picks", "selections", "draws", "bestDraws", "passed");

    private static IEnumerable<JsonElement> EnumerateNamedArray(JsonElement root, params string[] propertyNames)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                yield return element;
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var picks) && picks.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in picks.EnumerateArray())
                {
                    yield return element;
                }

                yield break;
            }
        }
    }

    private static bool TryReadPredictionId(JsonElement element, out int predictionId)
    {
        predictionId = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in new[] { "predictionId", "PredictionId", "id", "Id" })
        {
            if (element.TryGetProperty(propertyName, out var idElement) &&
                idElement.ValueKind == JsonValueKind.Number &&
                idElement.TryGetInt32(out predictionId) &&
                predictionId > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadReason(JsonElement element)
    {
        foreach (var propertyName in new[] { "reason", "Reason", "note", "Note", "justification" })
        {
            if (element.TryGetProperty(propertyName, out var reasonElement) &&
                reasonElement.ValueKind == JsonValueKind.String)
            {
                return reasonElement.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    [GeneratedRegex("\"(?:predictionId|PredictionId|id|Id)\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PredictionIdRegex();
}
