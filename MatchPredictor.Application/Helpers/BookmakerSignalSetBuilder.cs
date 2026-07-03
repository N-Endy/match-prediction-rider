using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Statistics;

namespace MatchPredictor.Application.Helpers;

/// <summary>
/// Matches each fixture in a prediction batch to the bookmaker pricing card and converts
/// the raw quotes into de-vigged fair probabilities (<see cref="SourceMarketDeVig"/>).
/// The result feeds the ensemble as the true market signal.
/// </summary>
public static class BookmakerSignalSetBuilder
{
    public static BookmakerSignalSet Build(
        IReadOnlyCollection<MatchData> matches,
        IReadOnlyList<SourceMarketFixture> sourceFixtures)
    {
        if (sourceFixtures.Count == 0 || matches.Count == 0)
        {
            return BookmakerSignalSet.Empty;
        }

        var signalSet = new BookmakerSignalSet();
        foreach (var match in matches)
        {
            var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
                sourceFixtures,
                match.HomeTeam,
                match.AwayTeam,
                match.League,
                match.MatchDateTime);

            if (sourceFixture is null)
            {
                continue;
            }

            signalSet.Add(match, SourceMarketDeVig.ToFairProbabilities(sourceFixture));
        }

        return signalSet;
    }
}
