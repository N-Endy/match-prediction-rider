using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Application.Helpers;

public static class BetslipSelectionHitMapper
{
    public static BetslipSelectionHitStatus Map(Prediction? prediction, DateTime utcNow)
    {
        if (prediction is null)
        {
            return BetslipSelectionHitStatus.Pending;
        }

        if (PredictionScoreClassHelper.IsActuallyLive(prediction, utcNow))
        {
            if (PredictionScoreClassHelper.IsLivePredictionCorrect(prediction))
            {
                return BetslipSelectionHitStatus.Won;
            }

            if (string.Equals(prediction.PredictionCategory, "Under2.5Goals", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(prediction.ActualScore)
                && !PredictionScoreClassHelper.IsPredictionCorrect(prediction))
            {
                return BetslipSelectionHitStatus.Lost;
            }

            return BetslipSelectionHitStatus.Live;
        }

        var settled = !string.IsNullOrWhiteSpace(prediction.ActualScore)
                      || !string.IsNullOrWhiteSpace(prediction.ActualOutcome);
        if (!settled)
        {
            return BetslipSelectionHitStatus.Pending;
        }

        return PredictionScoreClassHelper.IsPredictionCorrect(prediction)
            ? BetslipSelectionHitStatus.Won
            : BetslipSelectionHitStatus.Lost;
    }

    public static BetslipSelectionHitStatus MapSelection(
        BetslipSelection selection,
        DateOnly slipDate,
        IReadOnlyDictionary<int, Prediction> predictionsById,
        IReadOnlyList<Prediction> fallbackPredictions,
        DateTime utcNow)
    {
        return Map(Resolve(selection, slipDate, predictionsById, fallbackPredictions), utcNow);
    }

    public static BetslipHitStatus MapSlip(IReadOnlyCollection<BetslipSelectionHitStatus> legStatuses)
    {
        if (legStatuses.Count == 0)
        {
            return BetslipHitStatus.Pending;
        }

        if (legStatuses.Any(status => status == BetslipSelectionHitStatus.Lost))
        {
            return BetslipHitStatus.Lost;
        }

        if (legStatuses.Any(status => status == BetslipSelectionHitStatus.Live))
        {
            return BetslipHitStatus.Live;
        }

        if (legStatuses.All(status => status == BetslipSelectionHitStatus.Won))
        {
            return BetslipHitStatus.Won;
        }

        if (legStatuses.Any(status => status == BetslipSelectionHitStatus.Won) &&
            legStatuses.Any(status => status == BetslipSelectionHitStatus.Pending))
        {
            return BetslipHitStatus.Partial;
        }

        return BetslipHitStatus.Pending;
    }

    public static Prediction? Resolve(
        BetslipSelection selection,
        DateOnly slipDate,
        IReadOnlyDictionary<int, Prediction> predictionsById,
        IReadOnlyList<Prediction> fallbackPredictions)
    {
        Prediction? stored = null;
        if (selection.PredictionId is int predictionId)
        {
            predictionsById.TryGetValue(predictionId, out stored);
        }

        if (stored is not null && IsSettled(stored))
        {
            return stored;
        }

        var fallback = FindFallback(selection, slipDate, fallbackPredictions);
        return fallback ?? stored;
    }

    private static Prediction? FindFallback(
        BetslipSelection selection,
        DateOnly slipDate,
        IReadOnlyList<Prediction> fallbackPredictions)
    {
        if (fallbackPredictions.Count == 0)
        {
            return null;
        }

        var matchDate = selection.MatchDateTimeUtc is DateTime kickoffUtc
            ? DateTimeProvider.ConvertUtcToLocalDate(kickoffUtc)
            : slipDate;
        var home = Normalize(selection.HomeTeam);
        var away = Normalize(selection.AwayTeam);
        var market = Normalize(selection.Market);
        var category = InferCategory(selection);
        var candidates = fallbackPredictions
            .Where(prediction =>
                prediction.MatchLocalDate == matchDate &&
                Normalize(prediction.HomeTeam) == home &&
                Normalize(prediction.AwayTeam) == away)
            .ToList();

        var outcomeMatch = candidates.FirstOrDefault(prediction =>
            string.Equals(prediction.PredictedOutcome, selection.PredictedOutcome, StringComparison.OrdinalIgnoreCase));
        if (outcomeMatch is not null)
        {
            return outcomeMatch;
        }

        var categoryMatch = candidates.FirstOrDefault(prediction =>
            !string.IsNullOrWhiteSpace(category) &&
            string.Equals(prediction.PredictionCategory, category, StringComparison.OrdinalIgnoreCase));

        // Never fall back to an unrelated market on the same fixture.
        return categoryMatch;
    }

    private static string InferCategory(BetslipSelection selection)
    {
        var market = selection.Market ?? string.Empty;
        var outcome = selection.PredictedOutcome ?? string.Empty;
        if (market.Contains("BTTS", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("BTTS", StringComparison.OrdinalIgnoreCase))
        {
            return "BothTeamsScore";
        }

        if (market.Contains("Over 2.5", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("Over 2.5", StringComparison.OrdinalIgnoreCase))
        {
            return "Over2.5Goals";
        }

        if (market.Contains("Under 2.5", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("Under 2.5", StringComparison.OrdinalIgnoreCase))
        {
            return "Under2.5Goals";
        }

        if (market.Contains("Draw", StringComparison.OrdinalIgnoreCase) ||
            outcome.Equals("Draw", StringComparison.OrdinalIgnoreCase))
        {
            return "Draw";
        }

        if (market.Contains("1X2", StringComparison.OrdinalIgnoreCase) ||
            market.Contains("Straight", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("Win", StringComparison.OrdinalIgnoreCase))
        {
            return "StraightWin";
        }

        return string.Empty;
    }

    private static bool IsSettled(Prediction prediction) =>
        !string.IsNullOrWhiteSpace(prediction.ActualScore)
        || !string.IsNullOrWhiteSpace(prediction.ActualOutcome);

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();
}
