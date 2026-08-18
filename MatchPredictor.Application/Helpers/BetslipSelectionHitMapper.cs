using MatchPredictor.Domain.Models;

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
}
