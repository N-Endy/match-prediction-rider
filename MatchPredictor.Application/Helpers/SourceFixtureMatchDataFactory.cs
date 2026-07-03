using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Statistics;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Application.Helpers;

/// <summary>
/// Builds <see cref="MatchData"/> rows from bookmaker pricing when the sports-ai.dev
/// Excel feed is unavailable — graceful degradation path.
/// </summary>
public static class SourceFixtureMatchDataFactory
{
    public static List<MatchData> BuildFromSourceFixtures(
        IReadOnlyList<SourceMarketFixture> fixtures,
        DateOnly targetLocalDate)
    {
        var results = new List<MatchData>(fixtures.Count);

        foreach (var fixture in fixtures)
        {
            if (fixture.MatchTimeUtc is not { } kickoffUtc)
            {
                continue;
            }

            var localDate = DateTimeProvider.ConvertUtcToLocalDate(kickoffUtc);
            if (localDate != targetLocalDate)
            {
                continue;
            }

            var localTime = DateTimeProvider.ConvertUtcToLocalTime(kickoffUtc);
            var fair = SourceMarketDeVig.ToFairProbabilities(fixture);

            var match = new MatchData
            {
                Date = DateTimeProvider.FormatLocalDate(localDate),
                Time = DateTimeProvider.FormatLocalTime(localTime),
                MatchLocalDate = localDate,
                MatchLocalTime = localTime,
                MatchDateTime = kickoffUtc,
                League = fixture.League?.Trim(),
                HomeTeam = fixture.HomeTeam?.Trim(),
                AwayTeam = fixture.AwayTeam?.Trim(),
                HomeWin = fair.HomeWin ?? fixture.HomeWinProbability ?? 0,
                Draw = fair.Draw ?? fixture.DrawProbability ?? 0,
                AwayWin = fair.AwayWin ?? fixture.AwayWinProbability ?? 0,
                OverTwoGoals = fair.Over25 ?? fixture.Over25Probability ?? 0,
                UnderTwoGoals = fair.Under25 ?? fixture.Under25Probability ?? 0,
                BttsYes = fair.Btts ?? fixture.BttsYesProbability ?? 0,
                BttsNo = fair.Btts is { } bttsYes
                    ? 1.0 - bttsYes
                    : fixture.BttsNoProbability ?? 0
            };

            match.FixtureKey = FixtureIdentityFactory.FromMatchData(match).FixtureKey;
            match.NormalizeSourceProbabilities();
            results.Add(match);
        }

        return results;
    }
}
