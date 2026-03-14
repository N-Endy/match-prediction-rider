using System.Text;

namespace MatchPredictor.Web.Middleware;

public class AdminUsageBasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public AdminUsageBasicAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/admin/usage", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var username = _configuration["UsageDashboard:Username"]
                       ?? _configuration["Hangfire:Username"]
                       ?? "admin";
        var password = _configuration["UsageDashboard:Password"]
                       ?? _configuration["Hangfire:Password"]
                       ?? "changeme";

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!TryValidateBasicAuth(authHeader, username, password))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Usage Dashboard\"";
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
                   credentials[0] == expectedUsername &&
                   credentials[1] == expectedPassword;
        }
        catch
        {
            return false;
        }
    }
}
