using System.Security.Cryptography;
using System.Text;

namespace MatchPredictor.Web.Middleware;

/// <summary>
/// HTTP Basic Auth gate for operator-only surfaces: the usage dashboard,
/// the scrape status page, and the ops health page. These expose internal
/// state (errors, job health, calibration internals) and must never be public.
/// </summary>
public class AdminUsageBasicAuthMiddleware
{
    private static readonly string[] ProtectedPathPrefixes =
    [
        "/admin/usage",
        "/ScrapeStatus",
        "/ops/health"
    ];

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminUsageBasicAuthMiddleware> _logger;

    public AdminUsageBasicAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<AdminUsageBasicAuthMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isProtected = ProtectedPathPrefixes.Any(prefix =>
            context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
        if (!isProtected)
        {
            await _next(context);
            return;
        }

        var username = _configuration["UsageDashboard:Username"]
                       ?? _configuration["Hangfire:Username"];
        var password = _configuration["UsageDashboard:Password"]
                       ?? _configuration["Hangfire:Password"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogError("Admin dashboard credentials are not configured. Denying access to {Path}.", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Admin dashboard credentials are not configured.");
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!TryValidateBasicAuth(authHeader, username, password))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Admin Dashboard\"";
            return;
        }

        await _next(context);
    }

    private static bool TryValidateBasicAuth(string authHeader, string expectedUsername, string expectedPassword)
    {
        if (string.IsNullOrWhiteSpace(authHeader) ||
            !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var encodedCredentials = authHeader["Basic ".Length..].Trim();
            var decodedBytes = Convert.FromBase64String(encodedCredentials);
            var credentials = Encoding.UTF8.GetString(decodedBytes).Split(':', 2);

            return credentials.Length == 2 &&
                   FixedTimeEquals(credentials[0], expectedUsername) &&
                   FixedTimeEquals(credentials[1], expectedPassword);
        }
        catch
        {
            return false;
        }
    }

    internal static bool FixedTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
