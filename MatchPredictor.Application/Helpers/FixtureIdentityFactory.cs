using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Application.Helpers;

public static class FixtureIdentityFactory
{
    public static FixtureIdentity FromMatchData(MatchData match)
    {
        var localDate = match.MatchLocalDate ?? DeriveLocalDate(match.Date, match.MatchDateTime);
        var localTime = match.MatchLocalTime ?? DeriveLocalTime(match.Time, match.MatchDateTime);

        return Build(
            match.HomeTeam,
            match.AwayTeam,
            match.League,
            localDate,
            localTime,
            match.MatchDateTime);
    }

    public static FixtureIdentity FromPrediction(Prediction prediction)
    {
        var localDate = prediction.MatchLocalDate != default
            ? prediction.MatchLocalDate
            : DeriveLocalDate(prediction.Date, prediction.MatchDateTime) ?? default;
        var localTime = prediction.MatchLocalTime ?? DeriveLocalTime(prediction.Time, prediction.MatchDateTime);

        return Build(
            prediction.HomeTeam,
            prediction.AwayTeam,
            prediction.League,
            localDate,
            localTime,
            prediction.MatchDateTime);
    }

    public static FixtureIdentity FromForecast(ForecastObservation forecast)
    {
        var localDate = forecast.MatchLocalDate != default
            ? forecast.MatchLocalDate
            : DeriveLocalDate(forecast.Date, forecast.MatchDateTime) ?? default;
        var localTime = forecast.MatchLocalTime ?? DeriveLocalTime(forecast.Time, forecast.MatchDateTime);

        return Build(
            forecast.HomeTeam,
            forecast.AwayTeam,
            forecast.League,
            localDate,
            localTime,
            forecast.MatchDateTime);
    }

    public static FixtureIdentity FromSourceMarketFixture(SourceMarketFixture fixture)
    {
        var localDate = fixture.MatchTimeUtc.HasValue
            ? DateTimeProvider.ConvertUtcToLocalDate(fixture.MatchTimeUtc.Value)
            : (DateOnly?)null;
        var localTime = fixture.MatchTimeUtc.HasValue
            ? DateTimeProvider.ConvertUtcToLocalTime(fixture.MatchTimeUtc.Value)
            : (TimeOnly?)null;

        return Build(
            fixture.HomeTeam,
            fixture.AwayTeam,
            fixture.League,
            localDate,
            localTime,
            fixture.MatchTimeUtc);
    }

    public static FixtureIdentity Build(
        string? homeTeam,
        string? awayTeam,
        string? league,
        DateOnly? localDate,
        TimeOnly? localTime,
        DateTime? kickoffTimeUtc)
    {
        var normalizedHome = ScoreMatchingHelper.CreateTeamLookupKey(homeTeam, league);
        var normalizedAway = ScoreMatchingHelper.CreateTeamLookupKey(awayTeam, league);
        var normalizedLeague = ScoreMatchingHelper.CreateLeagueLookupKey(league);
        var dateComponent = localDate.HasValue
            ? DateTimeProvider.FormatLocalDate(localDate.Value)
            : (kickoffTimeUtc.HasValue ? DateTimeProvider.ConvertTimeToDateString(kickoffTimeUtc.Value) : "unknown-date");

        var fixtureKey = string.Join(
            "|",
            dateComponent.ToLowerInvariant(),
            normalizedLeague,
            normalizedHome,
            normalizedAway);

        return new FixtureIdentity(
            fixtureKey,
            league?.Trim() ?? string.Empty,
            homeTeam?.Trim() ?? string.Empty,
            awayTeam?.Trim() ?? string.Empty,
            localDate,
            localTime,
            kickoffTimeUtc);
    }

    private static DateOnly? DeriveLocalDate(string? legacyDate, DateTime? kickoffTimeUtc)
    {
        if (kickoffTimeUtc.HasValue)
        {
            return DateTimeProvider.ConvertUtcToLocalDate(kickoffTimeUtc.Value);
        }

        return DateTimeProvider.ParseLocalDateOrNull(legacyDate);
    }

    private static TimeOnly? DeriveLocalTime(string? legacyTime, DateTime? kickoffTimeUtc)
    {
        if (kickoffTimeUtc.HasValue)
        {
            return DateTimeProvider.ConvertUtcToLocalTime(kickoffTimeUtc.Value);
        }

        return DateTimeProvider.ParseLocalTimeOrNull(legacyTime);
    }
}
