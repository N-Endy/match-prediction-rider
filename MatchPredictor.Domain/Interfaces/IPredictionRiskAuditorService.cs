using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IPredictionRiskAuditorService
{
  Task AuditPublishedCandidatesAsync(
    IReadOnlyCollection<PredictionCandidate> publishedCandidates,
    CancellationToken cancellationToken = default);
}
