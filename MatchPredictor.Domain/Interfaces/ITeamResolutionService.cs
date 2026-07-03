namespace MatchPredictor.Domain.Interfaces;

public interface ITeamResolutionService
{
    Task SeedAliasesFromExistingDataAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, int>> LoadAliasLookupAsync(CancellationToken cancellationToken = default);
}
