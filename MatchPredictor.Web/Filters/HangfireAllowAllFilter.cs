using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using System.Text;

namespace MatchPredictor.Web.Filters;

/// <summary>
/// Allows all requests to the Hangfire dashboard.
/// Used in Development only — Production uses HangfireBasicAuthFilter.
/// </summary>
public class HangfireAllowAllFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}

/// <summary>
/// HTTP Basic Auth filter for Hangfire Dashboard in production.
/// Prompts the browser for username/password before granting access.
/// </summary>
public class HangfireBasicAuthFilter : IDashboardAuthorizationFilter
{
    private readonly string _username;
    private readonly string _password;

    public HangfireBasicAuthFilter(string username, string password)
    {
        _username = username;
        _password = password;
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var authHeader = httpContext.Request.Headers["Authorization"].FirstOrDefault();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic "))
        {
            SetChallengeResponse(httpContext);
            return false;
        }

        try
        {
            var encodedCredentials = authHeader["Basic ".Length..].Trim();
            var decodedBytes = Convert.FromBase64String(encodedCredentials);
            var credentials = Encoding.UTF8.GetString(decodedBytes).Split(':', 2);

            if (credentials.Length == 2 &&
                credentials[0] == _username &&
                credentials[1] == _password)
            {
                return true;
            }
        }
        catch
        {
            // Invalid base64 or format — fall through to challenge
        }

        SetChallengeResponse(httpContext);
        return false;
    }

    private static void SetChallengeResponse(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = 401;
        httpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
    }
}
