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

    public static Prediction? Resolve(
        BetslipSelection selection,
        DateOnly slipDate,
        IReadOnlyDictionary<int, Prediction> predictionsById,
        IReadOnlyList<Prediction> fallbackPredictions)
    {
        if (selection.PredictionId is int predictionId &&
            predictionsById.TryGetValue(predictionId, out var byId))
        {
            return byId;
        }

        if (fallbackPredictions.Count == 0)
        {
            return null;
        }

        var matchDate = selection.MatchDateTimeUtc is DateTime kickoffUtc
            ? DateTimeProvider.ConvertUtcToLocalDate(kickoffUtc)
            : slipDate;
        var home = Normalize(selection.HomeTeam);
        var away = Normalize(selection.AwayTeam);
        var candidates = fallbackPredictions
            .Where(prediction =>
                prediction.MatchLocalDate == matchDate &&
                Normalize(prediction.HomeTeam) == home &&
                Normalize(prediction.AwayTeam) == away)
            .ToList();

        return candidates.FirstOrDefault(prediction =>
                   string.Equals(prediction.PredictedOutcome, selection.PredictedOutcome, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();
}
