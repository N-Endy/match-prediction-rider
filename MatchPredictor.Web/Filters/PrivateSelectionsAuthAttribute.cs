using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatchPredictor.Infrastructure.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MatchPredictor.Web.Filters;

/// <summary>
/// Secures private owner-only surfaces (e.g. /Admin/Selections).
/// Requests without a valid passkey query parameter or active auth cookie return HTTP 404 (Not Found),
/// ensuring the page is completely invisible to public users, crawlers, and scrapers.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PrivateSelectionsAuthAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string CookieName = "mp_selections_auth";
    public const string QueryKeyName = "key";
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(60);
    private const string ProtectorPurpose = "MatchPredictor.Web.PrivateSelections.AuthTicket.v1";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var dataProtection = httpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
        var protector = dataProtection.CreateProtector(ProtectorPurpose);

        var configuredPasskey = configuration.GetSelectionsPasskey();
        if (string.IsNullOrWhiteSpace(configuredPasskey))
        {
            // If passkey is not configured in environment or appsettings, fail closed with 404.
            context.Result = new NotFoundResult();
            return;
        }

        // 1. Check if passkey was supplied via query string
        if (httpContext.Request.Query.TryGetValue(QueryKeyName, out var providedKeyValues))
        {
            var providedKey = providedKeyValues.ToString();
            if (FixedTimeEquals(providedKey, configuredPasskey))
            {
                // Set 60-day encrypted cookie
                SetAuthCookie(httpContext, protector);

                // Redirect to the same path without the ?key= parameter to keep browser history clean
                var cleanPath = httpContext.Request.Path;
                var queryDictionary = httpContext.Request.Query
                    .Where(kvp => !string.Equals(kvp.Key, QueryKeyName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var queryString = queryDictionary.Count > 0
                    ? QueryString.Create(queryDictionary).ToUriComponent()
                    : string.Empty;

                var redirectUrl = $"{cleanPath}{queryString}";
                context.Result = new RedirectResult(redirectUrl);
                return;
            }

            // Key was supplied but invalid - return 404 immediately
            context.Result = new NotFoundResult();
            return;
        }

        // 2. Check if valid encrypted cookie exists
        if (httpContext.Request.Cookies.TryGetValue(CookieName, out var cookieValue) &&
            !string.IsNullOrWhiteSpace(cookieValue))
        {
            if (TryValidateTicket(cookieValue, protector))
            {
                // Authenticated successfully; allow pipeline to proceed
                return;
            }
        }

        // 3. Neither valid key nor valid cookie: invisible 404
        context.Result = new NotFoundResult();
    }

    private static void SetAuthCookie(HttpContext httpContext, IDataProtector protector)
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(CookieLifetime);
        var payload = new AuthTicketPayload(expiresAtUtc, Guid.NewGuid().ToString("N"));
        var protectedTicket = protector.Protect(JsonSerializer.Serialize(payload));

        httpContext.Response.Cookies.Append(
            CookieName,
            protectedTicket,
            new CookieOptions
            {
                Expires = expiresAtUtc.UtcDateTime,
                HttpOnly = true,
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                IsEssential = true
            });
    }

    private static bool TryValidateTicket(string protectedTicket, IDataProtector protector)
    {
        try
        {
            var json = protector.Unprotect(protectedTicket);
            var payload = JsonSerializer.Deserialize<AuthTicketPayload>(json);
            return payload is not null && payload.ExpiresAtUtc > DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private sealed record AuthTicketPayload(DateTimeOffset ExpiresAtUtc, string Nonce);
}
