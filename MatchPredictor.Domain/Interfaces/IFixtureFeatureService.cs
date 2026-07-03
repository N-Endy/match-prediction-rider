using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IFixtureFeatureService
{
    Task CaptureFeatureSnapshotsAsync(
        IReadOnlyCollection<MatchData> fixtures,
        CancellationToken cancellationToken = default);
}
