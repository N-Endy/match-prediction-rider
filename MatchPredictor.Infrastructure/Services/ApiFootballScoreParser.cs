using System.Text.Json;

namespace MatchPredictor.Infrastructure.Services;

public static class ApiFootballScoreParser
{
    public static string? TryReadRegularTimeScore(JsonElement fixture)
    {
        if (!fixture.TryGetProperty("score", out var scoreElement) ||
            scoreElement.ValueKind != JsonValueKind.Object ||
            !scoreElement.TryGetProperty("fulltime", out var fullTime) ||
            fullTime.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryReadGoal(fullTime, "home", out var homeGoals) ||
            !TryReadGoal(fullTime, "away", out var awayGoals))
        {
            return null;
        }

        return $"{homeGoals}:{awayGoals}";
    }

    private static bool TryReadGoal(JsonElement parent, string name, out int goals)
    {
        goals = 0;
        if (!parent.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out goals);
    }
}
