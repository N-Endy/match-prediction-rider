using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IQuickSlipService
{
    Task<QuickSlipParseResultDto> ParseAndStageAsync(QuickSlipParseRequest request, CancellationToken ct = default);
}
