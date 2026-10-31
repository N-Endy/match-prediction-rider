using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IOperationsTipsterService
{
    Task<OperationsDossierDto> GetOperationsDossierAsync(DateOnly? targetDate = null, CancellationToken ct = default);
    Task<OperationsDossierDto> RefreshOperationsDossierAsync(DateOnly? targetDate = null, CancellationToken ct = default);
}
