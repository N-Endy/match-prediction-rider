using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IMarketTimingService
{
    Task<MarketTimingAdvisory> AnalyzeMarketTimingAsync(
        int predictionId,
        CancellationToken ct = default);

    Task<MarketTimingAdvisory> EvaluateOddsTrajectoryAsync(
        string fixtureKey,
        string market,
        string outcome,
        double currentOdds,
        DateTime matchDateTimeUtc,
        CancellationToken ct = default);

    Task CaptureInterimOddsSnapshotsAsync(CancellationToken ct = default);
}
