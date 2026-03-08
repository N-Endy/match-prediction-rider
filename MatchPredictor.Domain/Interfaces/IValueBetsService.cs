using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IValueBetsService
{
    Task<IEnumerable<ValueBetDto>> GetTopValueBetsAsync(int limit = 60, CancellationToken ct = default);
}
