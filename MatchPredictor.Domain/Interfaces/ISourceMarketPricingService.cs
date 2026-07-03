using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface ISourceMarketPricingService
{
    Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches source market pricing for fixtures on a specific local date (today or a
    /// future date). Implementations should return an empty list when the source cannot
    /// quote that day. The default implementation only supports today's card.
    /// </summary>
    Task<IReadOnlyList<SourceMarketFixture>> GetSourceMarketFixturesForDateAsync(
        DateOnly targetLocalDate,
        CancellationToken ct = default)
        => GetTodaySourceMarketFixturesAsync(ct);
}
