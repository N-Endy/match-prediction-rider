using System.Threading.RateLimiting;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace MatchPredictor.Web.Extensions;

public static class ApiRateLimitingExtensions
{
    public static IServiceCollection AddMatchPredictorRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RateLimiting");
                var policyName = context.HttpContext.GetEndpoint()?.Metadata
                    .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "unknown";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString("0");
                }

                logger.LogWarning(
                    "Rate limit rejected for policy {PolicyName} on {Path} from {ClientKey}.",
                    policyName,
                    context.HttpContext.Request.Path,
                    BuildClientKey(context.HttpContext, fallbackCookieName: null));

                if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
                {
                    context.HttpContext.Response.ContentType = "application/json";
                    await context.HttpContext.Response.WriteAsJsonAsync(
                        new { message = "Too many requests. Please slow down and try again shortly." },
                        cancellationToken: token);
                }
            };

            options.AddPolicy(
                RateLimitPolicies.AiChat,
                httpContext => RateLimitPartition.GetSlidingWindowLimiter(
                    BuildClientKey(httpContext, AiChatAuthDefaults.SessionCookieName),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 12,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(
                RateLimitPolicies.Booking,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    BuildClientKey(httpContext, "MP_VISITOR_SESSION"),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 6,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(
                RateLimitPolicies.Tracking,
                httpContext => RateLimitPartition.GetTokenBucketLimiter(
                    BuildClientKey(httpContext, "MP_VISITOR_SESSION"),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 120,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        TokensPerPeriod = 120,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        AutoReplenishment = true
                    }));
        });

        return services;
    }

    private static string BuildClientKey(HttpContext httpContext, string? fallbackCookieName)
    {
        if (!string.IsNullOrWhiteSpace(fallbackCookieName) &&
            httpContext.Request.Cookies.TryGetValue(fallbackCookieName, out var cookieValue) &&
            !string.IsNullOrWhiteSpace(cookieValue))
        {
            return $"cookie:{fallbackCookieName}:{cookieValue}";
        }

        var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var forwardedKey = forwardedFor.Split(',', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(forwardedKey))
            {
                return $"ip:{forwardedKey}";
            }
        }

        return $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
