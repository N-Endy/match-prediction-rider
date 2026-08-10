using System.Security.Cryptography;
using System.Text;
using MatchPredictor.Web.Services;

namespace MatchPredictor.Web.Middleware;

/// <summary>
/// HTTP Basic Auth gate for operator-only surfaces: the usage dashboard,
/// scrape status, ops health, analytics, and admin APIs. Also soft-detects
/// admin presence (Basic Auth header or AdminUi cookie) on public pages so
/// prediction cards can show operator-only controls.
/// </summary>
public class AdminUsageBasicAuthMiddleware
{
    private static readonly string[] ProtectedPathPrefixes =
    [
        "/admin",
        "/analytics",
        "/ScrapeStatus",
        "/ops/health"
    ];

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly IAdminUiTicketService _adminUiTicketService;
    private readonly ILogger<AdminUsageBasicAuthMiddleware> _logger;

    public AdminUsageBasicAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        IAdminUiTicketService adminUiTicketService,
        ILogger<AdminUsageBasicAuthMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _adminUiTicketService = adminUiTicketService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var username = _configuration["UsageDashboard:Username"]
                       ?? _configuration["Hangfire:Username"];
        var password = _configuration["UsageDashboard:Password"]
                       ?? _configuration["Hangfire:Password"];

        var authHeader = context.Request.Headers.Authorization.ToString();
        var hasValidBasicAuth = !string.IsNullOrWhiteSpace(username) &&
                                !string.IsNullOrWhiteSpace(password) &&
                                TryValidateBasicAuth(authHeader, username, password);
        var hasValidTicket = _adminUiTicketService.IsAuthenticated(context);

        if (hasValidBasicAuth || hasValidTicket)
        {
            context.Items[AdminOperatorKeys.HttpContextItem] = true;
        }

        var isProtected = ProtectedPathPrefixes.Any(prefix =>
            context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
        if (!isProtected)
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogError("Admin dashboard credentials are not configured. Denying access to {Path}.", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Admin dashboard credentials are not configured.");
            return;
        }

        if (!hasValidBasicAuth)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Admin Dashboard\"";
            return;
        }

        if (!hasValidTicket)
        {
            _adminUiTicketService.SignIn(context);
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
