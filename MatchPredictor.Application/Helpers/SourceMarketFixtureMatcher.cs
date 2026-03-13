using MatchPredictor.Domain.Models;

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
            var homeMatch = ScoreMatchingHelper.GetTeamMatchResult(homeTeam ?? string.Empty, fixture.HomeTeam, league, fixture.League);
            if (!homeMatch.IsMatch)
                continue;

            var awayMatch = ScoreMatchingHelper.GetTeamMatchResult(awayTeam ?? string.Empty, fixture.AwayTeam, league, fixture.League);
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
}
