namespace MatchPredictor.Domain.Models;

public static class MatchDataProbabilityExtensions
{
    public static double Over25(this MatchData match) => match.OverTwoGoals;

    public static double Over35(this MatchData match) => match.OverThreeGoals;

    public static double Under25(this MatchData match) => match.UnderTwoGoals;

    public static double Under35(this MatchData match) => match.UnderThreeGoals;

    public static double BttsYesProbability(this MatchData match) => match.BttsYes;

    public static double BttsNoProbability(this MatchData match) => match.BttsNo;

    public static void NormalizeSourceProbabilities(this MatchData match)
    {
        if (TryGetNormalizedOneX2(match, out var oneX2))
        {
            match.HomeWin = oneX2.home;
            match.Draw = oneX2.draw;
            match.AwayWin = oneX2.away;
        }

        if (TryGetNormalizedOver25Pair(match, out var overUnder25))
        {
            match.OverTwoGoals = overUnder25.over25;
            match.UnderTwoGoals = overUnder25.under25;
        }

        if (TryGetNormalizedBttsPair(match, out var bttsPair))
        {
            match.BttsYes = bttsPair.yes;
            match.BttsNo = bttsPair.no;
        }
    }

    public static bool TryGetNormalizedOneX2(this MatchData match, out (double home, double draw, double away) normalized)
    {
        normalized = default;

        if (match.HomeWin <= 0 || match.Draw <= 0 || match.AwayWin <= 0)
            return false;

        var total = match.HomeWin + match.Draw + match.AwayWin;
        if (total <= 0)
            return false;

        normalized = (match.HomeWin / total, match.Draw / total, match.AwayWin / total);
        return true;
    }

    public static bool TryGetNormalizedOver25Pair(this MatchData match, out (double over25, double under25) normalized)
    {
        normalized = default;

        if (match.OverTwoGoals <= 0 || match.UnderTwoGoals <= 0)
            return false;

        var total = match.OverTwoGoals + match.UnderTwoGoals;
        if (total <= 0)
            return false;

        normalized = (match.OverTwoGoals / total, match.UnderTwoGoals / total);
        return true;
    }

    public static bool TryGetNormalizedBttsPair(this MatchData match, out (double yes, double no) normalized)
    {
        normalized = default;

        if (match.BttsYes <= 0 || match.BttsNo <= 0)
            return false;

        var total = match.BttsYes + match.BttsNo;
        if (total <= 0)
            return false;

        normalized = (match.BttsYes / total, match.BttsNo / total);
        return true;
    }
}
