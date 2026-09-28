using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IDeepMatchResearchService
{
    Task<DeepMatchResearchDossier> ResearchFixtureAsync(
        string homeTeam,
        string awayTeam,
        DateTime? fixtureDateUtc = null,
        string? league = null,
        CancellationToken ct = default);
}
