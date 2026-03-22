using System.Text;

namespace MatchPredictor.Web.Middleware;

public class AdminUsageBasicAuthMiddleware
{
    private static readonly PathString AdminUsagePrefix = new("/admin/usage");
    private static readonly PathString OpsPrefix = new("/ops");
    private static readonly PathString LegacyHealthPrefix = new("/Health/Health");
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
        if (!TryResolveProtectedArea(context.Request.Path, out var areaName, out var username, out var password))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogError("{AreaName} credentials are not configured. Denying access to {Path}.", areaName, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync($"{areaName} credentials are not configured.");
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!TryValidateBasicAuth(authHeader, username, password))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{areaName}\"";
            return;
        }

        await _next(context);
    }

    private bool TryResolveProtectedArea(
        PathString path,
        out string areaName,
        out string? username,
        out string? password)
    {
        areaName = string.Empty;
        username = null;
        password = null;

        if (path.StartsWithSegments(AdminUsagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            areaName = "Usage Dashboard";
            username = _configuration["UsageDashboard:Username"] ?? _configuration["Hangfire:Username"];
            password = _configuration["UsageDashboard:Password"] ?? _configuration["Hangfire:Password"];
            return true;
        }

        if (path.StartsWithSegments(OpsPrefix, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments(LegacyHealthPrefix, StringComparison.OrdinalIgnoreCase))
        {
            areaName = "Operational Health";
            username = _configuration["HealthDashboard:Username"] ?? _configuration["Hangfire:Username"];
            password = _configuration["HealthDashboard:Password"] ?? _configuration["Hangfire:Password"];
            return true;
        }

        return false;
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
                   credentials[0] == expectedUsername &&
                   credentials[1] == expectedPassword;
        }
        catch
        {
            return false;
        }
    }
}
