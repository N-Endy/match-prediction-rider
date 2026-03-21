using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public static class MarketQuoteResolver
{
    public const string LivePricingSourceLabel = "Live source pull";
    public const string LiveOddsFreshnessLabel = "Fresh from today's source pricing pull.";
    public const string SourceOddsDerivationLabel = "Source decimal odds";
    public const string LiveDerivedOddsDerivationLabel = "Derived from live source probability";
    public const string LiveSourceName = "SportyBet";

    public static bool TryResolve(
        MatchData match,
        SourceMarketFixture? sourceFixture,
        PredictionMarket market,
        out MarketQuote quote)
    {
        quote = new MarketQuote();

        if (sourceFixture is null)
        {
            return false;
        }

        var liveProbability = GetLiveProbability(sourceFixture, market);
        var rawOdds = GetLiveRawOdds(sourceFixture, market);
        if (liveProbability is null or <= 0d)
        {
            return false;
        }

        var decimalOdds = rawOdds is > 1d
            ? rawOdds.Value
            : BetPricingMath.ConvertProbabilityToDecimalOdds(liveProbability);

        if (decimalOdds is null or <= 1d)
        {
            return false;
        }

        var impliedProbability = BetPricingMath.ConvertDecimalOddsToProbability(decimalOdds.Value);
        if (impliedProbability is null or <= 0d)
        {
            return false;
        }

        quote = new MarketQuote
        {
            MarketProbability = rawOdds is > 1d ? impliedProbability.Value : liveProbability.Value,
            DecimalOdds = decimalOdds.Value,
            ImpliedProbability = impliedProbability.Value,
            PricingSource = LivePricingSourceLabel,
            OddsFreshness = LiveOddsFreshnessLabel,
            OddsDerivationSource = rawOdds is > 1d ? SourceOddsDerivationLabel : LiveDerivedOddsDerivationLabel,
            SourceName = LiveSourceName
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
            "MatchWinner" when prediction.PredictedOutcome.Equals("Home Win", StringComparison.OrdinalIgnoreCase) => PredictionMarket.HomeWin,
            "MatchWinner" when prediction.PredictedOutcome.Equals("Away Win", StringComparison.OrdinalIgnoreCase) => PredictionMarket.AwayWin,
            _ => default
        };

        return prediction.PredictionCategory == "MatchWinner" &&
               market is PredictionMarket.HomeWin or PredictionMarket.AwayWin;
    }

    private static double? GetLiveProbability(SourceMarketFixture fixture, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => fixture.HomeWinProbability,
            PredictionMarket.AwayWin => fixture.AwayWinProbability,
            _ => null
        };
    }

    private static double? GetLiveRawOdds(SourceMarketFixture fixture, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => fixture.HomeWinOdds,
            PredictionMarket.AwayWin => fixture.AwayWinOdds,
            _ => null
        };
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
