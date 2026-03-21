using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public class ProbabilityCalculator : IProbabilityCalculator
{
    public MatchProbabilities CalculateProbabilities(MatchData match)
    {
        var winnerPair = NormalizePair(match.HomeWin, match.AwayWin);
        var totalsPair = NormalizePair(match.OverTwoPointFiveSets, match.UnderTwoPointFiveSets);
        var handicapPair = NormalizePair(match.SetHandicapHome, match.SetHandicapAway);

        return new MatchProbabilities(
            Over25Sets: totalsPair.home,
            Under25Sets: totalsPair.away,
            HomeWin: winnerPair.home,
            AwayWin: winnerPair.away,
            HomeSetHandicap: handicapPair.home,
            AwaySetHandicap: handicapPair.away);
    }

    public double CalculateOverTwoPointFiveSetsProbability(MatchData match)
    {
        return CalculateProbabilities(match).Over25Sets;
    }

    public double CalculateUnderTwoPointFiveSetsProbability(MatchData match)
    {
        return CalculateProbabilities(match).Under25Sets;
    }

    public double CalculateHomeWinProbability(MatchData match)
    {
        return CalculateProbabilities(match).HomeWin;
    }

    public double CalculateAwayWinProbability(MatchData match)
    {
        return CalculateProbabilities(match).AwayWin;
    }

    public double CalculateHomeSetHandicapProbability(MatchData match)
    {
        return CalculateProbabilities(match).HomeSetHandicap;
    }

    public double CalculateAwaySetHandicapProbability(MatchData match)
    {
        return CalculateProbabilities(match).AwaySetHandicap;
    }

    private static (double home, double away) NormalizePair(double home, double away)
    {
        home = Math.Clamp(home, 0.0, 1.0);
        away = Math.Clamp(away, 0.0, 1.0);

        if (home > 0 && away > 0)
        {
            var total = home + away;
            return total <= 0 ? (0.0, 0.0) : (home / total, away / total);
        }

        if (home > 0)
        {
            return (home, Math.Clamp(1.0 - home, 0.0, 1.0));
        }

        if (away > 0)
        {
            return (Math.Clamp(1.0 - away, 0.0, 1.0), away);
        }

        return (0.0, 0.0);
    }
}
