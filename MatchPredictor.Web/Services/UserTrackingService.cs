using System.Text.Json;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Web.Services;

public class UserTrackingService : IUserTrackingService
{
    private const string VisitorCookieName = "MP_VISITOR_ID";
    private const string SessionCookieName = "MP_VISITOR_SESSION";
    private const string ContextItemKey = "__mp_tracking_context";
    private static readonly TimeSpan VisitorCookieLifetime = TimeSpan.FromDays(180);
    private static readonly TimeSpan SessionCookieLifetime = TimeSpan.FromHours(12);

    private static readonly HashSet<string> AllowedEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "page_view",
        "ai_chat_request",
        "booking_attempt",
        "booking_success",
        "booking_failure",
        "add_to_cart",
        "clear_cart",
        "open_betslip",
        "copy_booking_code"
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<UserTrackingService> _logger;

    public UserTrackingService(ApplicationDbContext dbContext, ILogger<UserTrackingService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task EnsureTrackingContextAsync(HttpContext httpContext, CancellationToken ct = default)
    {
        try
        {
            await EnsureTrackingContextCoreAsync(httpContext, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize tracking context for path {Path}.", httpContext.Request.Path);
        }
    }

    public async Task TrackPageViewAsync(HttpContext httpContext, CancellationToken ct = default)
    {
        try
        {
            if (!ShouldTrackPath(httpContext.Request.Path))
            {
                return;
            }

            var trackingContext = await EnsureTrackingContextCoreAsync(httpContext, ct);
            if (trackingContext.SkipTracking)
            {
                return;
            }

            _dbContext.UserActivityEvents.Add(new UserActivityEvent
            {
                VisitorId = trackingContext.VisitorId,
                SessionId = trackingContext.SessionId,
                EventType = "page_view",
                Path = NormalizePath(httpContext.Request.Path),
                MetadataJson = SerializeMetadata(new Dictionary<string, string?>
                {
                    ["referrer"] = trackingContext.Referrer
                })
            });

            await _dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to track page view for path {Path}.", httpContext.Request.Path);
        }
    }

    public async Task TrackEventAsync(
        HttpContext httpContext,
        string eventType,
        string? pagePath = null,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken ct = default)
    {
        if (!AllowedEventTypes.Contains(eventType))
        {
            _logger.LogDebug("Skipping unsupported tracking event type {EventType}.", eventType);
            return;
        }

        try
        {
            var trackingContext = await EnsureTrackingContextCoreAsync(httpContext, ct);
            if (trackingContext.SkipTracking)
            {
                return;
            }

            _dbContext.UserActivityEvents.Add(new UserActivityEvent
            {
                VisitorId = trackingContext.VisitorId,
                SessionId = trackingContext.SessionId,
                EventType = eventType,
                Path = NormalizePath(pagePath ?? httpContext.Request.Path),
                MetadataJson = SerializeMetadata(metadata)
            });

            await _dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to track event {EventType} for path {Path}.", eventType, pagePath ?? httpContext.Request.Path.Value);
        }
    }

    public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var since24Hours = now.AddHours(-24);
        var since7Days = now.AddDays(-7);

        var sessionsLast24Hours = _dbContext.VisitorSessions
            .AsNoTracking()
            .Where(session => session.LastSeenAt >= since24Hours);

        var sessionsLast7Days = _dbContext.VisitorSessions
            .AsNoTracking()
            .Where(session => session.LastSeenAt >= since7Days);

        var eventsLast7Days = _dbContext.UserActivityEvents
            .AsNoTracking()
            .Where(activity => activity.CreatedAt >= since7Days);

        return new UsageSnapshot
        {
            UniqueVisitorsLast24Hours = await sessionsLast24Hours.Select(session => session.VisitorId).Distinct().CountAsync(ct),
            UniqueVisitorsLast7Days = await sessionsLast7Days.Select(session => session.VisitorId).Distinct().CountAsync(ct),
            SessionsLast7Days = await sessionsLast7Days.CountAsync(ct),
            PageViewsLast7Days = await eventsLast7Days.CountAsync(activity => activity.EventType == "page_view", ct),
            AiChatRequestsLast7Days = await eventsLast7Days.CountAsync(activity => activity.EventType == "ai_chat_request", ct),
            CartAddsLast7Days = await eventsLast7Days.CountAsync(activity => activity.EventType == "add_to_cart", ct),
            BookingAttemptsLast7Days = await eventsLast7Days.CountAsync(activity => activity.EventType == "booking_attempt", ct),
            SuccessfulBookingsLast7Days = await eventsLast7Days.CountAsync(activity => activity.EventType == "booking_success", ct),
            TopPagesLast7Days = await eventsLast7Days
                .Where(activity => activity.EventType == "page_view")
                .GroupBy(activity => activity.Path)
                .Select(group => new UsagePathSummary
                {
                    Path = group.Key,
                    Count = group.Count()
                })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Path)
                .Take(8)
                .ToListAsync(ct)
        };
    }

    private async Task<TrackingContext> EnsureTrackingContextCoreAsync(HttpContext httpContext, CancellationToken ct)
    {
        if (httpContext.Items.TryGetValue(ContextItemKey, out var existing) && existing is TrackingContext cached)
        {
            return cached;
        }

        var userAgent = (httpContext.Request.Headers.UserAgent.ToString() ?? string.Empty).Trim();
        if (IsBot(userAgent))
        {
            var skipped = new TrackingContext(skipTracking: true);
            httpContext.Items[ContextItemKey] = skipped;
            return skipped;
        }

        var visitorId = ReadCookie(httpContext, VisitorCookieName);
        if (string.IsNullOrWhiteSpace(visitorId))
        {
            visitorId = Guid.NewGuid().ToString("N");
            httpContext.Response.Cookies.Append(VisitorCookieName, visitorId, BuildCookieOptions(httpContext, DateTime.UtcNow.Add(VisitorCookieLifetime)));
        }

        var sessionId = ReadCookie(httpContext, SessionCookieName);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
            httpContext.Response.Cookies.Append(SessionCookieName, sessionId, BuildCookieOptions(httpContext, DateTime.UtcNow.Add(SessionCookieLifetime)));
        }
        else
        {
            httpContext.Response.Cookies.Append(SessionCookieName, sessionId, BuildCookieOptions(httpContext, DateTime.UtcNow.Add(SessionCookieLifetime)));
        }

        var path = NormalizePath(httpContext.Request.Path);
        var referrer = TrimToLength(httpContext.Request.Headers.Referer.ToString(), 512);
        var now = DateTime.UtcNow;

        var session = await _dbContext.VisitorSessions.SingleOrDefaultAsync(item => item.SessionId == sessionId, ct);
        if (session is null)
        {
            session = new VisitorSession
            {
                VisitorId = visitorId,
                SessionId = sessionId,
                LandingPath = path,
                LastPath = path,
                Referrer = referrer,
                UserAgent = TrimToLength(userAgent, 512),
                FirstSeenAt = now,
                LastSeenAt = now
            };

            _dbContext.VisitorSessions.Add(session);
        }
        else
        {
            session.LastSeenAt = now;
            session.LastPath = path;
            if (string.IsNullOrWhiteSpace(session.Referrer))
            {
                session.Referrer = referrer;
            }

            if (string.IsNullOrWhiteSpace(session.UserAgent))
            {
                session.UserAgent = TrimToLength(userAgent, 512);
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        var context = new TrackingContext(visitorId, sessionId, path, referrer);
        httpContext.Items[ContextItemKey] = context;
        return context;
    }

    private static bool ShouldTrackPath(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        var value = path.Value!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !value.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("/hangfire", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("/js", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("/images", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("/lib", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(PathString path) => NormalizePath(path.Value);

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? "/"
            : path.Trim();
    }

    private static string ReadCookie(HttpContext httpContext, string cookieName)
    {
        if (httpContext.Items.TryGetValue($"{ContextItemKey}:{cookieName}", out var cachedValue) && cachedValue is string cachedText)
        {
            return cachedText;
        }

        var value = httpContext.Request.Cookies[cookieName] ?? string.Empty;
        httpContext.Items[$"{ContextItemKey}:{cookieName}"] = value;
        return value;
    }

    private static CookieOptions BuildCookieOptions(HttpContext httpContext, DateTime expiresAt)
    {
        return new CookieOptions
        {
            Expires = expiresAt,
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true
        };
    }

    private static bool IsBot(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return false;
        }

        return userAgent.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
               userAgent.Contains("spider", StringComparison.OrdinalIgnoreCase) ||
               userAgent.Contains("crawler", StringComparison.OrdinalIgnoreCase) ||
               userAgent.Contains("facebookexternalhit", StringComparison.OrdinalIgnoreCase) ||
               userAgent.Contains("preview", StringComparison.OrdinalIgnoreCase);
    }

    private static string SerializeMetadata(IReadOnlyDictionary<string, string?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return string.Empty;
        }

        return JsonSerializer.Serialize(metadata
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => TrimToLength(pair.Value, 256)));
    }

    private static string TrimToLength(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private sealed class TrackingContext
    {
        public TrackingContext(
            string visitorId = "",
            string sessionId = "",
            string landingPath = "",
            string referrer = "",
            bool skipTracking = false)
        {
            VisitorId = visitorId;
            SessionId = sessionId;
            LandingPath = landingPath;
            Referrer = referrer;
            SkipTracking = skipTracking;
        }

        public string VisitorId { get; }
        public string SessionId { get; }
        public string LandingPath { get; }
        public string Referrer { get; }
        public bool SkipTracking { get; }
    }
}
