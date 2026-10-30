using System.Globalization;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Services;

public static partial class QuickSlipParser
{
    private static readonly Dictionary<string, int> WordCounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["single"] = 1,
        ["double"] = 2,
        ["couple"] = 2,
        ["treble"] = 3,
        ["triple"] = 3,
        ["few"] = 3,
        ["fourfold"] = 4,
        ["several"] = 4,
        ["fivefold"] = 5,
        ["handful"] = 5
    };

    private static readonly Dictionary<string, string> LeagueAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["epl"] = "Premier League",
        ["premier league"] = "Premier League",
        ["la liga"] = "LaLiga",
        ["laliga"] = "LaLiga",
        ["serie a"] = "Serie A",
        ["bundesliga"] = "Bundesliga",
        ["ligue 1"] = "Ligue 1",
        ["champions league"] = "Champions League",
        ["ucl"] = "Champions League",
        ["europa league"] = "Europa League"
    };

    public static QuickSlipParsedIntent Parse(string input, DateOnly? referenceDate = null)
    {
        var today = referenceDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(input))
        {
            return new QuickSlipParsedIntent(
                [], null, null, null, null, "Balanced", null, [], today, true,
                ["Tell us what you're looking for, e.g. '3 safe EPL home wins' or '4 GG matches tonight'."]);
        }

        var text = input.Trim();
        var lower = text.ToLowerInvariant();

        var questions = new List<string>();
        var markets = DetectMarkets(lower);
        var count = DetectCount(lower);
        var (minOdds, maxOdds, targetCombinedOdds) = DetectOddsConstraints(lower);
        var bias = DetectBias(lower);
        var league = DetectLeague(lower);
        var teams = DetectTeams(text);
        var (targetDate, isTonight) = DetectTargetDate(lower, today);

        // If no market was named and no team specified, ask clarification
        if (markets.Count == 0 && teams.Count == 0)
        {
            questions.Add("Which market do you prefer? (e.g. BTTS, Over 2.5 Goals, Straight Win, or Draw)");
        }

        var isAmbiguous = questions.Count > 0;

        return new QuickSlipParsedIntent(
            markets,
            count,
            minOdds,
            maxOdds,
            targetCombinedOdds,
            bias,
            league,
            teams,
            targetDate,
            isAmbiguous,
            questions,
            isTonight);
    }

    private static List<PredictionMarket> DetectMarkets(string lower)
    {
        var markets = new HashSet<PredictionMarket>();

        // BTTS / GG
        if (Regex.IsMatch(lower, @"\b(btts|gg|goal\s*goal|both\s*teams?\s*(to)?\s*score)\b"))
        {
            markets.Add(PredictionMarket.BothTeamsScore);
        }

        // Over 2.5
        if (Regex.IsMatch(lower, @"\b(o2\.5|over\s*2\.5|over\s*2|overs|high\s*scoring)\b"))
        {
            markets.Add(PredictionMarket.Over25Goals);
        }

        // Under 2.5
        if (Regex.IsMatch(lower, @"\b(u2\.5|under\s*2\.5|under\s*2|unders|low\s*scoring)\b"))
        {
            markets.Add(PredictionMarket.Under25Goals);
        }

        // Straight Win / 1X2
        if (Regex.IsMatch(lower, @"\b(straight\s*wins?|home\s*wins?|away\s*wins?|1x2|winners?|wins?)\b") &&
            !Regex.IsMatch(lower, @"\b(over\s*2|under\s*2|btts|gg)\b"))
        {
            markets.Add(PredictionMarket.StraightWin);
        }

        // Draw
        if (Regex.IsMatch(lower, @"\b(draw|draws|stalemate|tie)\b"))
        {
            markets.Add(PredictionMarket.Draw);
        }

        return markets.ToList();
    }

    private static int? DetectCount(string lower)
    {
        // Check word numbers first ("3-fold", "4 fold", "double", "treble")
        foreach (var (word, val) in WordCounts)
        {
            if (Regex.IsMatch(lower, $@"\b{word}\b"))
            {
                return val;
            }
        }

        var match = Regex.Match(lower, @"\b(?<num>\d{1,2})\s*(-|\s)?(fold|legs?|picks?|games?|bets?|matches?)\b");
        if (match.Success && int.TryParse(match.Groups["num"].Value, out var count) && count is > 0 and <= 20)
        {
            return count;
        }

        var loneDigitMatch = Regex.Match(lower, @"^(give\s*me\s*|build\s*me\s*|find\s*|select\s*|get\s*me\s*|i\s*need\s*|i\s*want\s*)?(?<num>\d{1,2})\s+[a-z]");
        if (loneDigitMatch.Success && int.TryParse(loneDigitMatch.Groups["num"].Value, out var loneCount) && loneCount is > 0 and <= 20)
        {
            return loneCount;
        }

        var marketCountMatch = Regex.Match(lower, @"\b(?<num>\d{1,2})\s+(btts|gg|both\s*teams|over|under|straight|wins?|draws?|accas?|combos?|fixtures?)\b");
        if (marketCountMatch.Success && int.TryParse(marketCountMatch.Groups["num"].Value, out var mCount) && mCount is > 0 and <= 20)
        {
            return mCount;
        }

        return null;
    }

    private static (decimal? minOdds, decimal? maxOdds, decimal? targetCombined) DetectOddsConstraints(string lower)
    {
        decimal? minOdds = null;
        decimal? maxOdds = null;
        decimal? targetCombined = null;

        // Between X and Y
        var betweenMatch = Regex.Match(lower, @"(between|from)\s+(?<min>\d+(\.\d+)?)\s+(and|to|-)\s+(?<max>\d+(\.\d+)?)(\s*odds)?");
        if (betweenMatch.Success)
        {
            if (decimal.TryParse(betweenMatch.Groups["min"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var bMin)) minOdds = bMin;
            if (decimal.TryParse(betweenMatch.Groups["max"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var bMax)) maxOdds = bMax;
            return (minOdds, maxOdds, targetCombined);
        }

        // Min Odds: "odds > 1.5", "odds over 1.8", "above 1.5 odds", "at least 1.6 odds", "1.50+"
        var minMatch = Regex.Match(lower, @"(odds\s*(>|>=|over|above|at\s*least|min(imum)?)\s*|\babove\s*)(?<val>\d+(\.\d+)?)");
        if (minMatch.Success && decimal.TryParse(minMatch.Groups["val"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedMin))
        {
            minOdds = parsedMin;
        }

        // Max Odds: "odds < 2.5", "odds under 2.0", "below 2.0 odds", "max 2.2 odds"
        var maxMatch = Regex.Match(lower, @"(odds\s*(<|<=|under|below|max(imum)?)\s*|\bunder\s*)(?<val>\d+(\.\d+)?)");
        if (maxMatch.Success && decimal.TryParse(maxMatch.Groups["val"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedMax))
        {
            maxOdds = parsedMax;
        }

        // Target combined odds: "target 5 odds", "5x odds", "accumulate to 10 odds", "total odds 5.0"
        var combinedMatch = Regex.Match(lower, @"(target|total|combined|accumulate\s*to)\s*(?<val>\d+(\.\d+)?)\s*odds");
        if (combinedMatch.Success && decimal.TryParse(combinedMatch.Groups["val"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedCombined))
        {
            targetCombined = parsedCombined;
        }

        return (minOdds, maxOdds, targetCombined);
    }

    private static string DetectBias(string lower)
    {
        if (Regex.IsMatch(lower, @"\b(safe|safest|banker|bankers|lock|locks|high\s*probability|high\s*confidence|solid|sure)\b"))
        {
            return "Safe";
        }

        if (Regex.IsMatch(lower, @"\b(value|value\s*bets?|edge|high\s*odds|underdog|underdogs|longshot|positive\s*ev)\b"))
        {
            return "Value";
        }

        return "Balanced";
    }

    private static string? DetectLeague(string lower)
    {
        foreach (var (alias, canonical) in LeagueAliases)
        {
            if (Regex.IsMatch(lower, $@"\b{Regex.Escape(alias)}\b"))
            {
                return canonical;
            }
        }

        return null;
    }

    private static List<string> DetectTeams(string text)
    {
        var teams = new List<string>();
        var knownTeams = new[]
        {
            "Arsenal", "Chelsea", "Liverpool", "Manchester City", "Man City", "Manchester United", "Man United",
            "Tottenham", "Spurs", "Newcastle", "Aston Villa", "Barcelona", "Real Madrid", "Atletico Madrid",
            "Bayern Munich", "Bayern", "Borussia Dortmund", "Dortmund", "Paris Saint-Germain", "PSG",
            "Juventus", "Inter Milan", "Inter", "AC Milan", "Milan", "Napoli", "Roma"
        };

        foreach (var team in knownTeams)
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(team)}\b", RegexOptions.IgnoreCase))
            {
                teams.Add(team);
            }
        }

        return teams;
    }

    private static (DateOnly TargetDate, bool IsTonight) DetectTargetDate(string lower, DateOnly today)
    {
        var isTonight = Regex.IsMatch(lower, @"\b(tonight|this\s*evening|night)\b");

        if (lower.Contains("tomorrow"))
        {
            return (today.AddDays(1), false);
        }

        if (lower.Contains("weekend") || lower.Contains("saturday") || lower.Contains("sunday"))
        {
            var daysUntilSaturday = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7;
            if (daysUntilSaturday == 0 && today.DayOfWeek != DayOfWeek.Saturday) daysUntilSaturday = 7;
            return (today.AddDays(daysUntilSaturday), false);
        }

        return (today, isTonight);
    }
}
