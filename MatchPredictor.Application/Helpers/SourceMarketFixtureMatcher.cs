using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Helpers;

namespace MatchPredictor.Application.Helpers;

public static class SourceMarketFixtureMatcher
{
    private static readonly TimeSpan TightKickoffWindow = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan LooseKickoffWindow = TimeSpan.FromHours(3);
    private static readonly TimeSpan MaxKickoffWindow = TimeSpan.FromHours(8);

    public static SourceMarketFixture? FindBestFixture(
        IEnumerable<SourceMarketFixture> fixtures,
        string? homeTeam,
        string? awayTeam,
        string? league,
        DateTime? scheduledUtc)
    {
        SourceMarketFixture? bestFixture = null;
        var bestScore = 0.0;

        foreach (var fixture in fixtures)
        {
            var homeMatch = AreCanonicalAliasesEqual(homeTeam, fixture.HomeTeam, league, fixture.League)
                ? new ScoreMatchingHelper.TeamMatchResult(true, 1.0, true, false)
                : ScoreMatchingHelper.GetTeamMatchResult(homeTeam ?? string.Empty, fixture.HomeTeam, league, fixture.League);
            if (!homeMatch.IsMatch)
                continue;

            var awayMatch = AreCanonicalAliasesEqual(awayTeam, fixture.AwayTeam, league, fixture.League)
                ? new ScoreMatchingHelper.TeamMatchResult(true, 1.0, true, false)
                : ScoreMatchingHelper.GetTeamMatchResult(awayTeam ?? string.Empty, fixture.AwayTeam, league, fixture.League);
            if (!awayMatch.IsMatch)
                continue;

            var score = homeMatch.Score + awayMatch.Score;
            if (!string.IsNullOrWhiteSpace(league) && !string.IsNullOrWhiteSpace(fixture.League))
            {
                score += ScoreMatchingHelper.GetLeagueMatchScore(league, fixture.League) * 0.2;
            }

            if (scheduledUtc.HasValue && fixture.MatchTimeUtc.HasValue)
            {
                var kickoffDelta = (fixture.MatchTimeUtc.Value - scheduledUtc.Value).Duration();
                if (kickoffDelta > MaxKickoffWindow)
                    continue;

                if (kickoffDelta <= TightKickoffWindow)
                {
                    score += 0.25;
                }
                else if (kickoffDelta <= LooseKickoffWindow)
                {
                    score += 0.10;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestFixture = fixture;
            }
        }

        return bestScore >= 1.55 ? bestFixture : null;
    }

    private static bool AreCanonicalAliasesEqual(string? leftTeam, string? rightTeam, string? leftLeague, string? rightLeague)
    {
        var leftKey = TeamNameNormalizer.BuildAliasKey(leftTeam, leftLeague);
        var rightKey = TeamNameNormalizer.BuildAliasKey(rightTeam, rightLeague);
        if (!string.IsNullOrWhiteSpace(leftKey) && string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            TeamNameNormalizer.NormalizeAlias(leftTeam),
            TeamNameNormalizer.NormalizeAlias(rightTeam),
            StringComparison.OrdinalIgnoreCase);
    }
}
