using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Infrastructure.Services;

internal static class PointInTimeBacktestingSelector
{
    private static readonly TimeSpan KickoffGrace = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<ForecastObservation> SelectForecasts(IEnumerable<ForecastObservation> forecasts)
    {
        return SelectSnapshots(
            forecasts,
            forecast => BuildFixtureKey(
                forecast.FixtureKey,
                forecast.MatchLocalDate,
                forecast.League,
                forecast.HomeTeam,
                forecast.AwayTeam),
            forecast => forecast.Market,
            forecast => ResolveKickoffUtc(
                forecast.MatchDateTime,
                forecast.MatchLocalDate,
                forecast.MatchLocalTime,
                forecast.Date,
                forecast.Time),
            forecast => forecast.CreatedAt,
            forecast => forecast.RevisionNumber);
    }

    public static IReadOnlyList<Prediction> SelectPredictions(IEnumerable<Prediction> predictions)
    {
        return SelectSnapshots(
            predictions,
            prediction => BuildFixtureKey(
                prediction.FixtureKey,
                prediction.MatchLocalDate,
                prediction.League,
                prediction.HomeTeam,
                prediction.AwayTeam),
            prediction => prediction.PredictionCategory,
            prediction => ResolveKickoffUtc(
                prediction.MatchDateTime,
                prediction.MatchLocalDate,
                prediction.MatchLocalTime,
                prediction.Date,
                prediction.Time),
            prediction => prediction.CreatedAt,
            prediction => prediction.RevisionNumber);
    }

    private static IReadOnlyList<T> SelectSnapshots<T, TMarket>(
        IEnumerable<T> items,
        Func<T, string> fixtureKeySelector,
        Func<T, TMarket> marketSelector,
        Func<T, DateTime?> kickoffSelector,
        Func<T, DateTime> createdAtSelector,
        Func<T, int> revisionSelector)
        where TMarket : notnull
    {
        return items
            .GroupBy(item => (FixtureKey: fixtureKeySelector(item), Market: marketSelector(item)))
            .Select(group =>
            {
                var withKickoff = group
                    .Select(item => new SnapshotCandidate<T>(
                        item,
                        kickoffSelector(item),
                        createdAtSelector(item),
                        revisionSelector(item)))
                    .ToList();

                var eligible = withKickoff
                    .Where(candidate => candidate.KickoffUtc is null || candidate.CreatedAtUtc <= candidate.KickoffUtc.Value.Add(KickoffGrace))
                    .OrderByDescending(candidate => candidate.CreatedAtUtc)
                    .ThenByDescending(candidate => candidate.RevisionNumber)
                    .ToList();

                return eligible.Count > 0 ? eligible[0].Item : default;
            })
            .Where(item => item is not null)
            .Cast<T>()
            .ToList();
    }

    private static string BuildFixtureKey(
        string? fixtureKey,
        DateOnly localDate,
        string? league,
        string? homeTeam,
        string? awayTeam)
    {
        if (!string.IsNullOrWhiteSpace(fixtureKey))
        {
            return fixtureKey.Trim();
        }

        return string.Join(
            "|",
            localDate.ToString("yyyy-MM-dd"),
            Normalize(league),
            Normalize(homeTeam),
            Normalize(awayTeam));
    }

    private static DateTime? ResolveKickoffUtc(
        DateTime? storedKickoffUtc,
        DateOnly localDate,
        TimeOnly? localTime,
        string? date,
        string? time)
    {
        if (storedKickoffUtc.HasValue)
        {
            return storedKickoffUtc.Value;
        }

        if (localDate != default && localTime.HasValue)
        {
            return DateTimeProvider.ConvertLocalToUtc(
                localDate.ToDateTime(localTime.Value, DateTimeKind.Unspecified));
        }

        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time))
        {
            return null;
        }

        var parsed = DateTimeProvider.ParseCanonicalMatchDateTime(date, time);
        return parsed.utcDateTime;
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private sealed record SnapshotCandidate<T>(
        T Item,
        DateTime? KickoffUtc,
        DateTime CreatedAtUtc,
        int RevisionNumber);
}
