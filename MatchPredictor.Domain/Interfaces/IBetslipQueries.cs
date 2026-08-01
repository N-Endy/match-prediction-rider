using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IBetslipQueries
{
    Task<BetslipSet?> GetCurrentSetAsync(CancellationToken ct = default);
}
