using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public static class MarketQuoteResolver
{
    public const string LivePricingSourceLabel = "Live source pull";
    public const string StoredPricingSourceLabel = "Stored sync snapshot";
    public const string LiveOddsFreshnessLabel = "Fresh from today's source pricing pull.";
    public const string StoredOddsFreshnessLabel = "Using the latest stored sync pricing for this fixture.";
    public const string SourceOddsDerivationLabel = "Source decimal odds";
    public const string LiveDerivedOddsDerivationLabel = "Derived from live source probability";
    public const string StoredDerivedOddsDerivationLabel = "Derived from stored sync probability";
    public const string LiveSourceName = "SportyBet";
    public const string StoredSourceName = "Stored sync snapshot";

    public static bool TryResolve(
        MatchData match,
        SourceMarketFixture? sourceFixture,
        PredictionMarket market,
        out MarketQuote quote)
    {
        quote = new MarketQuote();

        var liveProbability = GetLiveProbability(sourceFixture, market);
        var storedProbability = GetStoredProbability(match, market);
        var selectedProbability = liveProbability ?? storedProbability;

        if (selectedProbability is null or <= 0d)
        {
            return false;
        }

        var rawOdds = GetLiveRawOdds(sourceFixture, market);
        var decimalOdds = rawOdds is > 1d
            ? rawOdds.Value
            : liveProbability is > 0d
                ? BetPricingMath.ConvertProbabilityToDecimalOdds(liveProbability) ?? 0d
                : BetPricingMath.ConvertProbabilityToDecimalOdds(storedProbability) ?? 0d;

        if (decimalOdds <= 1d)
        {
            return false;
        }

        var impliedProbability = BetPricingMath.ConvertDecimalOddsToProbability(decimalOdds) ?? 0d;
        if (impliedProbability <= 0d)
        {
            return false;
        }

        // When pricing from raw source odds, remove the bookmaker overround so edges are
        // measured against a fair probability — consistent with the normalized stored
        // probabilities used on the other path. Without this, raw-odds quotes carry the
        // vig and systematically understate edges relative to derived quotes.
        var effectiveMarketProbability = rawOdds is > 1d
            ? DeVigImpliedProbability(sourceFixture, market, impliedProbability)
            : selectedProbability.Value;

        quote = new MarketQuote
        {
            MarketProbability = effectiveMarketProbability,
            DecimalOdds = decimalOdds,
            ImpliedProbability = impliedProbability,
            PricingSource = liveProbability is > 0d ? LivePricingSourceLabel : StoredPricingSourceLabel,
            OddsFreshness = liveProbability is > 0d ? LiveOddsFreshnessLabel : StoredOddsFreshnessLabel,
            OddsDerivationSource = rawOdds is > 1d
                ? SourceOddsDerivationLabel
                : liveProbability is > 0d
                    ? LiveDerivedOddsDerivationLabel
                    : StoredDerivedOddsDerivationLabel,
            SourceName = liveProbability is > 0d ? LiveSourceName : StoredSourceName
        };
        return true;
    }

    public static bool TryResolve(
        MatchData match,
        Prediction prediction,
        SourceMarketFixture? sourceFixture,
        out MarketQuote quote)
    {
        quote = new MarketQuote();

        if (!TryResolvePredictionMarket(prediction, out var market))
        {
            return false;
        }

        return TryResolve(match, sourceFixture, market, out quote);
    }

    private static bool TryResolvePredictionMarket(Prediction prediction, out PredictionMarket market)
    {
        market = prediction.PredictionCategory switch
        {
            "BothTeamsScore" => PredictionMarket.BothTeamsScore,
            "Over2.5Goals" => PredictionMarket.Over25Goals,
            "Under2.5Goals" => PredictionMarket.Under25Goals,
            "Draw" => PredictionMarket.Draw,
            "StraightWin" when prediction.PredictedOutcome.Equals("Home Win", StringComparison.OrdinalIgnoreCase) => PredictionMarket.HomeWin,
            "StraightWin" when prediction.PredictedOutcome.Equals("Away Win", StringComparison.OrdinalIgnoreCase) => PredictionMarket.AwayWin,
            _ => default
        };

        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => true,
            "Over2.5Goals" => true,
            "Under2.5Goals" => true,
            "Draw" => true,
            "StraightWin" => market is PredictionMarket.HomeWin or PredictionMarket.AwayWin,
            _ => false
        };
    }

    private static double DeVigImpliedProbability(
        SourceMarketFixture? sourceFixture,
        PredictionMarket market,
        double impliedProbability)
    {
        var outcomeOdds = market switch
        {
            PredictionMarket.HomeWin or PredictionMarket.Draw or PredictionMarket.AwayWin =>
                new[] { sourceFixture?.HomeWinOdds, sourceFixture?.DrawOdds, sourceFixture?.AwayWinOdds },
            PredictionMarket.Over25Goals or PredictionMarket.Under25Goals =>
                new[] { sourceFixture?.Over25Odds, sourceFixture?.Under25Odds },
            PredictionMarket.BothTeamsScore =>
                new[] { sourceFixture?.BttsYesOdds, sourceFixture?.BttsNoOdds },
            _ => null
        };

        // Only de-vig when the full outcome set is priced; otherwise the overround
        // cannot be measured and the raw implied probability is the best available.
        if (outcomeOdds is null || outcomeOdds.Any(odds => odds is not > 1d))
        {
            return impliedProbability;
        }

        var overround = outcomeOdds.Sum(odds => 1d / odds!.Value);
        return overround > 0d
            ? Math.Clamp(impliedProbability / overround, 0d, 1d)
            : impliedProbability;
    }

    private static double? GetLiveProbability(SourceMarketFixture? sourceFixture, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => sourceFixture?.HomeWinProbability,
            PredictionMarket.Draw => sourceFixture?.DrawProbability,
            PredictionMarket.AwayWin => sourceFixture?.AwayWinProbability,
            PredictionMarket.Over25Goals => sourceFixture?.Over25Probability,
            PredictionMarket.Under25Goals => sourceFixture?.Under25Probability,
            PredictionMarket.BothTeamsScore => sourceFixture?.BttsYesProbability,
            _ => null
        };
    }

    private static double? GetLiveRawOdds(SourceMarketFixture? sourceFixture, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => sourceFixture?.HomeWinOdds,
            PredictionMarket.Draw => sourceFixture?.DrawOdds,
            PredictionMarket.AwayWin => sourceFixture?.AwayWinOdds,
            PredictionMarket.Over25Goals => sourceFixture?.Over25Odds,
            PredictionMarket.Under25Goals => sourceFixture?.Under25Odds,
            PredictionMarket.BothTeamsScore => sourceFixture?.BttsYesOdds,
            _ => null
        };
    }

    private static double? GetStoredProbability(MatchData match, PredictionMarket market)
    {
        if (market is PredictionMarket.HomeWin or PredictionMarket.Draw or PredictionMarket.AwayWin &&
            match.TryGetNormalizedOneX2(out var oneX2))
        {
            return market switch
            {
                PredictionMarket.HomeWin => oneX2.home,
                PredictionMarket.Draw => oneX2.draw,
                PredictionMarket.AwayWin => oneX2.away,
                _ => null
            };
        }

        if (market == PredictionMarket.Over25Goals && match.TryGetNormalizedOver25Pair(out var overUnder25))
        {
            return overUnder25.over25;
        }

        if (market == PredictionMarket.Under25Goals && match.TryGetNormalizedOver25Pair(out var underOver25))
        {
            return underOver25.under25;
        }

        if (market == PredictionMarket.BothTeamsScore && match.TryGetNormalizedBttsPair(out var bttsPair))
        {
            return bttsPair.yes;
        }

        return null;
    }
}

public sealed class MarketQuote
{
    public double MarketProbability { get; init; }
    public double DecimalOdds { get; init; }
    public double ImpliedProbability { get; init; }
    public string PricingSource { get; init; } = string.Empty;
    public string OddsFreshness { get; init; } = string.Empty;
    public string OddsDerivationSource { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
}
