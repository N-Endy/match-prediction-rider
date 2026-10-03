using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IFeatureDriftMonitorService
{
    Task<FeatureDriftReport> ComputeDriftReportAsync(
        int referenceWindowDays = 90,
        int currentWindowDays = 30,
        CancellationToken ct = default);
}
