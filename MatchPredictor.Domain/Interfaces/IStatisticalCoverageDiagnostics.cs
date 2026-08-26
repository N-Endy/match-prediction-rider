using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IStatisticalCoverageDiagnostics
{
    /// <summary>
    /// Reports how much history Dixon-Coles / features / xG ingest currently see,
    /// for comparing post-retention coverage against the old 90-day baseline.
    /// </summary>
    Task<StatisticalCoverageReport> MeasureAsync(CancellationToken cancellationToken = default);

    /// <summary>Hangfire entry point: measure and log coverage.</summary>
    Task RunNightlyDiagnosticsAsync(CancellationToken cancellationToken = default);
}
