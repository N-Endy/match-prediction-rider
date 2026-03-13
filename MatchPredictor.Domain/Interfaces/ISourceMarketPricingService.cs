using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface ISourceMarketPricingService
{
    Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default);
}
