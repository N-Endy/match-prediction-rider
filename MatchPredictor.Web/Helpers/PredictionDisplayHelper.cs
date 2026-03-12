using System.Globalization;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Web.Helpers;

public static partial class PredictionDisplayHelper
{
    public static string FormatWatTime(Prediction prediction)
    {
        if (DateTime.TryParse(
                prediction.Time,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
        {
            return parsedTime.ToString("h:mm tt");
        }

        if (prediction.MatchDateTime.HasValue)
        {
            return DateTimeProvider.ConvertUtcToLocal(prediction.MatchDateTime.Value).ToString("h:mm tt");
        }

        return prediction.Time;
    }

    public static bool IsActuallyLive(Prediction prediction, DateTime utcNow)
    {
        if (!prediction.IsLive)
        {
            return false;
        }

        return !prediction.MatchDateTime.HasValue || utcNow <= prediction.MatchDateTime.Value.AddMinutes(200);
    }

    public static bool IsLivePredictionCorrect(Prediction prediction)
    {
        if (!TryParseScore(prediction.ActualScore, out var homeGoals, out var awayGoals))
        {
            return false;
        }

        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => homeGoals > 0 && awayGoals > 0,
            "Over2.5Goals" => homeGoals + awayGoals > 2,
            _ => prediction.PredictedOutcome == prediction.ActualOutcome
        };
    }

    private static bool TryParseScore(string? score, out int homeGoals, out int awayGoals)
    {
        homeGoals = 0;
        awayGoals = 0;

        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var scoreParts = ScoreRegex()
            .Matches(score)
            .Select(match => match.Value)
            .ToArray();

        return scoreParts.Length >= 2
            && int.TryParse(scoreParts[0], out homeGoals)
            && int.TryParse(scoreParts[1], out awayGoals);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex ScoreRegex();
}
