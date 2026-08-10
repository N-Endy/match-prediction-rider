namespace MatchPredictor.Domain.Interfaces;

public interface ITeamResolutionService
{
    Task SeedAliasesFromExistingDataAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, int>> LoadAliasLookupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Links scraped team names as aliases of the prediction team names so later
    /// auto-settlement can rematch via exact alias pair.
    /// </summary>
    Task EnsureManualConfirmAliasesAsync(
        string predictionHomeTeam,
        string predictionAwayTeam,
        string scrapedHomeTeam,
        string scrapedAwayTeam,
        string? league,
        CancellationToken cancellationToken = default);
}
