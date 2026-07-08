using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Application.Helpers;

public static class ScoreSnapshotKeyFactory
{
    public static void Apply(MatchScore score)
    {
        ApplySnapshotKeys(
            score.MatchTime,
            score.HomeTeam,
            score.AwayTeam,
            score.League,
            out var matchLocalDate,
            out var homeTeamKey,
            out var awayTeamKey,
            out var leagueKey);

        score.MatchLocalDate = matchLocalDate;
        score.HomeTeamKey = homeTeamKey;
        score.AwayTeamKey = awayTeamKey;
        score.LeagueKey = leagueKey;
    }

    public static void Apply(AiScoreMatchScore score)
    {
        ApplySnapshotKeys(
            score.MatchTime,
            score.HomeTeam,
            score.AwayTeam,
            score.League,
            out var matchLocalDate,
            out var homeTeamKey,
            out var awayTeamKey,
            out var leagueKey);

        score.MatchLocalDate = matchLocalDate;
        score.HomeTeamKey = homeTeamKey;
        score.AwayTeamKey = awayTeamKey;
        score.LeagueKey = leagueKey;
    }

    public static void Apply(SofaScoreMatchScore score)
    {
        ApplySnapshotKeys(
            score.MatchTime,
            score.HomeTeam,
            score.AwayTeam,
            score.League,
            out var matchLocalDate,
            out var homeTeamKey,
            out var awayTeamKey,
            out var leagueKey);

        score.MatchLocalDate = matchLocalDate;
        score.HomeTeamKey = homeTeamKey;
        score.AwayTeamKey = awayTeamKey;
        score.LeagueKey = leagueKey;
    }

    public static (DateOnly MatchLocalDate, string HomeTeamKey, string AwayTeamKey, string LeagueKey) Create(
        DateTime matchTimeUtc,
        string homeTeam,
        string awayTeam,
        string league)
    {
        ApplySnapshotKeys(
            matchTimeUtc,
            homeTeam,
            awayTeam,
            league,
            out var matchLocalDate,
            out var homeTeamKey,
            out var awayTeamKey,
            out var leagueKey);

        return (matchLocalDate, homeTeamKey, awayTeamKey, leagueKey);
    }

    private static void ApplySnapshotKeys(
        DateTime matchTimeUtc,
        string homeTeam,
        string awayTeam,
        string league,
        out DateOnly matchLocalDate,
        out string homeTeamKey,
        out string awayTeamKey,
        out string leagueKey)
    {
        matchLocalDate = DateTimeProvider.ConvertUtcToLocalDate(matchTimeUtc);
        homeTeamKey = BoundKey(ScoreMatchingHelper.CreateTeamLookupKey(homeTeam, league));
        awayTeamKey = BoundKey(ScoreMatchingHelper.CreateTeamLookupKey(awayTeam, league));
        leagueKey = BoundKey(ScoreMatchingHelper.CreateLeagueLookupKey(league));
    }

    private static string BoundKey(string value)
    {
        return value.Length <= 512 ? value : value[..512];
    }
}
