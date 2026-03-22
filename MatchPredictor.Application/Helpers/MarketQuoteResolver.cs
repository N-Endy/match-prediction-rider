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

        var predictionCategory = market.ToCategory();
        var predictedOutcome = BuildPredictionOutcome(match, market);
        if (string.IsNullOrWhiteSpace(predictedOutcome))
        {
            return false;
        }

        var resolvedSelection = FindSelection(sourceFixture, predictionCategory, predictedOutcome);
        var liveProbability = resolvedSelection?.Probability ?? GetLegacyLiveProbability(sourceFixture, market);
        var rawOdds = resolvedSelection?.DecimalOdds ?? GetLegacyLiveRawOdds(sourceFixture, market);
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

        if (sourceFixture is null)
        {
            return false;
        }

        if (!TryResolvePredictionMarket(prediction, out var market))
        {
            return false;
        }

        return TryResolve(match, sourceFixture, market, prediction.PredictedOutcome, out quote);
    }

    private static bool TryResolvePredictionMarket(Prediction prediction, out PredictionMarket market)
    {
        market = prediction.PredictionCategory switch
        {
            "MatchWinner" when prediction.PredictedOutcome.Equals("Home Win", StringComparison.OrdinalIgnoreCase) => PredictionMarket.HomeWin,
            "MatchWinner" when prediction.PredictedOutcome.Equals("Away Win", StringComparison.OrdinalIgnoreCase) => PredictionMarket.AwayWin,
            "OverUnderSets" when prediction.PredictedOutcome.Equals("Over 2.5 Sets", StringComparison.OrdinalIgnoreCase) => PredictionMarket.Over25Sets,
            "OverUnderSets" when prediction.PredictedOutcome.Equals("Under 2.5 Sets", StringComparison.OrdinalIgnoreCase) => PredictionMarket.Under25Sets,
            "SetHandicap" when prediction.PredictedOutcome.StartsWith("Home ", StringComparison.OrdinalIgnoreCase) => PredictionMarket.HomeSetHandicap,
            "SetHandicap" when prediction.PredictedOutcome.StartsWith("Away ", StringComparison.OrdinalIgnoreCase) => PredictionMarket.AwaySetHandicap,
            _ => default
        };

        return prediction.PredictionCategory is "MatchWinner" or "OverUnderSets" or "SetHandicap";
    }

    private static bool TryResolve(
        MatchData match,
        SourceMarketFixture sourceFixture,
        PredictionMarket market,
        string predictedOutcome,
        out MarketQuote quote)
    {
        quote = new MarketQuote();

        var normalizedCategory = market.ToCategory();
        var resolvedSelection = FindSelection(sourceFixture, normalizedCategory, predictedOutcome);
        var liveProbability = resolvedSelection?.Probability ?? GetLegacyLiveProbability(sourceFixture, market);
        var rawOdds = resolvedSelection?.DecimalOdds ?? GetLegacyLiveRawOdds(sourceFixture, market);
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

    private static SourceMarketSelection? FindSelection(
        SourceMarketFixture fixture,
        string predictionCategory,
        string predictedOutcome)
    {
        return fixture.MarketSelections.FirstOrDefault(selection =>
            string.Equals(NormalizeKey(selection.Market), NormalizeKey(predictionCategory), StringComparison.Ordinal) &&
            string.Equals(NormalizeKey(selection.Prediction), NormalizeKey(predictedOutcome), StringComparison.Ordinal));
    }

    private static double? GetLegacyLiveProbability(SourceMarketFixture fixture, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => fixture.HomeWinProbability,
            PredictionMarket.AwayWin => fixture.AwayWinProbability,
            _ => null
        };
    }

    private static double? GetLegacyLiveRawOdds(SourceMarketFixture fixture, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => fixture.HomeWinOdds,
            PredictionMarket.AwayWin => fixture.AwayWinOdds,
            _ => null
        };
    }

    private static string BuildPredictionOutcome(MatchData match, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => "Home Win",
            PredictionMarket.AwayWin => "Away Win",
            PredictionMarket.Over25Sets => "Over 2.5 Sets",
            PredictionMarket.Under25Sets => "Under 2.5 Sets",
            PredictionMarket.HomeSetHandicap => BuildSetHandicapPrediction(match, true),
            PredictionMarket.AwaySetHandicap => BuildSetHandicapPrediction(match, false),
            _ => string.Empty
        };
    }

    private static string BuildSetHandicapPrediction(MatchData match, bool homeSide)
    {
        var line = Math.Abs(match.SetHandicapLine) > 0 ? match.SetHandicapLine : -1.5;
        var sideLine = homeSide ? line : -line;
        var side = homeSide ? "Home" : "Away";
        return $"{side} {sideLine:+0.0;-0.0} Sets";
    }

    private static string NormalizeKey(string? value)
    {
        return string.Join(
            " ",
            (value ?? string.Empty)
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
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
