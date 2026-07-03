using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Statistics;

/// <summary>
/// Blends probabilities from multiple models. Blending happens in <em>logit space</em>
/// (a weighted geometric mean of odds) which is the standard, well-behaved way to
/// pool calibrated probabilities. The 1X2 triple is renormalized to sum to 1 and
/// Under 2.5 is forced to be the complement of Over 2.5 for internal consistency.
/// </summary>
public static class EnsembleProbabilityBlender
{
    /// <summary>Weighted mean of probabilities in logit space; ignores null inputs.</summary>
    public static double BlendLogit(params (double? Probability, double Weight)[] signals)
    {
        var weightedLogitSum = 0.0;
        var totalWeight = 0.0;

        foreach (var (probability, weight) in signals)
        {
            if (probability is null || weight <= 0)
            {
                continue;
            }

            var clamped = Math.Clamp(probability.Value, 1e-6, 1.0 - 1e-6);
            weightedLogitSum += weight * Math.Log(clamped / (1.0 - clamped));
            totalWeight += weight;
        }

        if (totalWeight <= 0)
        {
            return 0.5;
        }

        var logit = weightedLogitSum / totalWeight;
        return 1.0 / (1.0 + Math.Exp(-logit));
    }

    public static MatchProbabilities Blend(
        MatchProbabilities? market,
        MatchProbabilities? baseModel,
        MatchProbabilities? dixonColes,
        MatchProbabilities? elo,
        EnsembleWeights? weights = null)
    {
        return Blend(bookmaker: null, market, baseModel, dixonColes, elo, weights);
    }

    public static MatchProbabilities Blend(
        PartialMatchProbabilities? bookmaker,
        MatchProbabilities? market,
        MatchProbabilities? baseModel,
        MatchProbabilities? dixonColes,
        MatchProbabilities? elo,
        EnsembleWeights? weights = null)
    {
        weights ??= EnsembleWeights.Default;

        var homeWin = BlendLogit(
            (bookmaker?.HomeWin, weights.Bookmaker),
            (market?.HomeWin, weights.Market),
            (baseModel?.HomeWin, weights.Base),
            (dixonColes?.HomeWin, weights.DixonColes),
            (elo?.HomeWin, weights.Elo));

        var draw = BlendLogit(
            (bookmaker?.Draw, weights.Bookmaker),
            (market?.Draw, weights.Market),
            (baseModel?.Draw, weights.Base),
            (dixonColes?.Draw, weights.DixonColes),
            (elo?.Draw, weights.Elo));

        var awayWin = BlendLogit(
            (bookmaker?.AwayWin, weights.Bookmaker),
            (market?.AwayWin, weights.Market),
            (baseModel?.AwayWin, weights.Base),
            (dixonColes?.AwayWin, weights.DixonColes),
            (elo?.AwayWin, weights.Elo));

        var over25 = BlendLogit(
            (bookmaker?.Over25, weights.Bookmaker),
            (market?.Over25, weights.Market),
            (baseModel?.Over25, weights.Base),
            (dixonColes?.Over25, weights.DixonColes));

        var btts = BlendLogit(
            (bookmaker?.Btts, weights.Bookmaker),
            (market?.Btts, weights.Market),
            (baseModel?.Btts, weights.Base),
            (dixonColes?.Btts, weights.DixonColes));

        var total = homeWin + draw + awayWin;
        if (total > 0)
        {
            homeWin /= total;
            draw /= total;
            awayWin /= total;
        }

        return new MatchProbabilities(
            Btts: Math.Clamp(btts, 0.0, 1.0),
            Over25: Math.Clamp(over25, 0.0, 1.0),
            Under25: Math.Clamp(1.0 - over25, 0.0, 1.0),
            Draw: Math.Clamp(draw, 0.0, 1.0),
            HomeWin: Math.Clamp(homeWin, 0.0, 1.0),
            AwayWin: Math.Clamp(awayWin, 0.0, 1.0));
    }
}
