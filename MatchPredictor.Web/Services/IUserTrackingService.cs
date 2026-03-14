using MatchPredictor.Domain.Models;

namespace MatchPredictor.Web.Services;

public interface IUserTrackingService
{
    Task EnsureTrackingContextAsync(HttpContext httpContext, CancellationToken ct = default);
    Task TrackPageViewAsync(HttpContext httpContext, CancellationToken ct = default);
    Task TrackEventAsync(
        HttpContext httpContext,
        string eventType,
        string? pagePath = null,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken ct = default);
    Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken ct = default);
}
