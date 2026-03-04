using System.Text.RegularExpressions;

namespace MatchPredictor.Domain.Helpers;

/// <summary>
/// Unified score parsing utility. Handles all separator formats: ":", "-", "–", "—"
/// and whitespace-padded variants ("1 - 0", "1:0", "1 – 0").
/// </summary>
public static class ScoreParser
{
    private static readonly Regex ScoreRegex = new(@"(\d+)\s*[-:–—]\s*(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Attempts to parse a score string into home and away goals.
    /// Supports formats: "1:0", "1-0", "1 - 0", "1–0", "1—0".
    /// </summary>
    public static bool TryParse(string? score, out int home, out int away)
    {
        home = 0;
        away = 0;

        if (string.IsNullOrWhiteSpace(score))
            return false;

        var match = ScoreRegex.Match(score.Trim());
        if (!match.Success)
            return false;

        return int.TryParse(match.Groups[1].Value, out home) &&
               int.TryParse(match.Groups[2].Value, out away);
    }

    /// <summary>
    /// Determines if both teams scored (BTTS) from a score string.
    /// </summary>
    public static bool IsBtts(string? score)
    {
        return TryParse(score, out var home, out var away) && home > 0 && away > 0;
    }
}
