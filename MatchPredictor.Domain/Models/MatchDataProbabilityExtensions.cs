namespace MatchPredictor.Domain.Models;

public static class MatchDataProbabilityExtensions
{
    public static double Over25Sets(this MatchData match) => match.OverTwoPointFiveSets;

    public static double Under25Sets(this MatchData match) => match.UnderTwoPointFiveSets;

    public static void NormalizeSourceProbabilities(this MatchData match)
    {
        if (TryGetNormalizedMatchWinnerPair(match, out var winnerPair))
        {
            match.HomeWin = winnerPair.home;
            match.AwayWin = winnerPair.away;
        }

        if (TryGetNormalizedOver25SetsPair(match, out var overUnder25))
        {
            match.OverTwoPointFiveSets = overUnder25.over25;
            match.UnderTwoPointFiveSets = overUnder25.under25;
        }

        if (TryGetNormalizedSetHandicapPair(match, out var handicapPair))
        {
            match.SetHandicapHome = handicapPair.home;
            match.SetHandicapAway = handicapPair.away;
        }
    }

    public static bool TryGetNormalizedMatchWinnerPair(this MatchData match, out (double home, double away) normalized)
    {
        normalized = default;

        if (match.HomeWin <= 0 || match.AwayWin <= 0)
            return false;

        var total = match.HomeWin + match.AwayWin;
        if (total <= 0)
            return false;

        normalized = (match.HomeWin / total, match.AwayWin / total);
        return true;
    }

    public static bool TryGetNormalizedOver25SetsPair(this MatchData match, out (double over25, double under25) normalized)
    {
        normalized = default;

        if (match.OverTwoPointFiveSets <= 0 || match.UnderTwoPointFiveSets <= 0)
            return false;

        var total = match.OverTwoPointFiveSets + match.UnderTwoPointFiveSets;
        if (total <= 0)
            return false;

        normalized = (match.OverTwoPointFiveSets / total, match.UnderTwoPointFiveSets / total);
        return true;
    }

    public static bool TryGetNormalizedSetHandicapPair(this MatchData match, out (double home, double away) normalized)
    {
        return TryGetNormalizedPair(match.SetHandicapHome, match.SetHandicapAway, out normalized);
    }

    private static bool TryGetNormalizedPair(double home, double away, out (double home, double away) normalized)
    {
        normalized = default;

        if (home <= 0 || away <= 0)
            return false;

        var total = home + away;
        if (total <= 0)
            return false;

        normalized = (home / total, away / total);
        return true;
    }
}
