using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatchPredictor.Application.Helpers;

public static partial class ValueBetJustificationParser
{
    public static Dictionary<string, string> Parse(string aiResponseJson)
    {
        if (string.IsNullOrWhiteSpace(aiResponseJson))
        {
            throw new JsonException("AI Value Bets response was empty.");
        }

        var normalized = NormalizeAiJson(aiResponseJson);
        if (TryParseJustifications(normalized, out var justifications) && justifications.Count > 0)
        {
            return justifications;
        }

        if (TrySalvageJustifications(normalized, out var salvaged) && salvaged.Count > 0)
        {
            return salvaged;
        }

        throw new JsonException("AI Value Bets response did not contain a usable picks array.");
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

    private static bool TryParseJustifications(string json, out Dictionary<string, string> justifications)
    {
        justifications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryReadJustifications(document.RootElement, justifications);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadJustifications(JsonElement root, Dictionary<string, string> justifications)
    {
        var picksElement = root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("picks", out var wrappedPicks))
        {
            picksElement = wrappedPicks;
        }

        if (picksElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var pickElement in picksElement.EnumerateArray())
        {
            TryAddJustification(pickElement, justifications);
        }

        return justifications.Count > 0;
    }

    private static bool TrySalvageJustifications(string json, out Dictionary<string, string> justifications)
    {
        justifications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Recover complete pick objects even when Gemini truncates mid-string later in the payload.
        foreach (Match match in CompletePickObjectRegex().Matches(json))
        {
            try
            {
                using var document = JsonDocument.Parse(match.Value);
                TryAddJustification(document.RootElement, justifications);
            }
            catch (JsonException)
            {
                // Ignore incomplete fragments.
            }
        }

        return justifications.Count > 0;
    }

    private static void TryAddJustification(JsonElement pickElement, Dictionary<string, string> justifications)
    {
        if (pickElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!TryGetStringProperty(pickElement, out var candidateKey, "CandidateKey", "candidateKey") ||
            !TryGetStringProperty(pickElement, out var justification, "AiJustification", "aiJustification"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(candidateKey) || string.IsNullOrWhiteSpace(justification))
        {
            return;
        }

        justifications[candidateKey] = justification.Trim();
    }

    private static bool TryGetStringProperty(JsonElement element, out string? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }
        }

        value = null;
        return false;
    }

    [GeneratedRegex(
        """\{\s*"(?:CandidateKey|candidateKey)"\s*:\s*"(?:\\.|[^"\\])*"\s*,\s*"(?:AiJustification|aiJustification)"\s*:\s*"(?:\\.|[^"\\])*"\s*\}|\{\s*"(?:AiJustification|aiJustification)"\s*:\s*"(?:\\.|[^"\\])*"\s*,\s*"(?:CandidateKey|candidateKey)"\s*:\s*"(?:\\.|[^"\\])*"\s*\}""",
        RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex CompletePickObjectRegex();
}
