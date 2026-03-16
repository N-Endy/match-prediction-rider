using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MatchPredictor.Web.Services;

public interface IAiChatAuthTicketService
{
    bool TryValidate(HttpContext httpContext, out string? sessionId);
    bool IsAuthenticated(HttpContext httpContext);
    string SignIn(HttpContext httpContext);
    void SignOut(HttpContext httpContext);
}

public static class AiChatAuthDefaults
{
    public const string AuthCookieName = "MP_AI_AUTH";
    public const string SessionCookieName = "MP_AI_CHAT_SESSION";
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(12);
}

public sealed class AiChatAuthTicketService : IAiChatAuthTicketService
{
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ticketLifetime;

    public AiChatAuthTicketService(
        IDataProtectionProvider dataProtectionProvider,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("MatchPredictor.Web.AiChat.AuthTicket.v1");
        _timeProvider = timeProvider;
        _ticketLifetime = ResolveTicketLifetime(configuration);
    }

    public bool TryValidate(HttpContext httpContext, out string? sessionId)
    {
        sessionId = null;

        var protectedTicket = httpContext.Request.Cookies[AiChatAuthDefaults.AuthCookieName];
        var cookieSessionId = httpContext.Request.Cookies[AiChatAuthDefaults.SessionCookieName];

        if (string.IsNullOrWhiteSpace(protectedTicket) || string.IsNullOrWhiteSpace(cookieSessionId))
        {
            return false;
        }

        AiChatTicketPayload? payload;
        try
        {
            var json = _protector.Unprotect(protectedTicket);
            payload = JsonSerializer.Deserialize<AiChatTicketPayload>(json);
        }
        catch
        {
            SignOut(httpContext);
            return false;
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.SessionId) ||
            string.IsNullOrWhiteSpace(payload.UserAgentHash) ||
            payload.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            !FixedEquals(payload.SessionId, cookieSessionId) ||
            !FixedEquals(payload.UserAgentHash, HashUserAgent(httpContext)))
        {
            SignOut(httpContext);
            return false;
        }

        sessionId = cookieSessionId;
        return true;
    }

    public bool IsAuthenticated(HttpContext httpContext) => TryValidate(httpContext, out _);

    public string SignIn(HttpContext httpContext)
    {
        var sessionId = httpContext.Request.Cookies[AiChatAuthDefaults.SessionCookieName];
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
        }

        var now = _timeProvider.GetUtcNow();
        var expiresAtUtc = now.Add(_ticketLifetime);

        var payload = new AiChatTicketPayload(
            sessionId,
            HashUserAgent(httpContext),
            expiresAtUtc,
            Guid.NewGuid().ToString("N"));

        var protectedTicket = _protector.Protect(JsonSerializer.Serialize(payload));

        httpContext.Response.Cookies.Append(
            AiChatAuthDefaults.SessionCookieName,
            sessionId,
            BuildCookieOptions(httpContext, expiresAtUtc));

        httpContext.Response.Cookies.Append(
            AiChatAuthDefaults.AuthCookieName,
            protectedTicket,
            BuildCookieOptions(httpContext, expiresAtUtc));

        return sessionId;
    }

    public void SignOut(HttpContext httpContext)
    {
        httpContext.Response.Cookies.Delete(AiChatAuthDefaults.AuthCookieName, BuildDeleteCookieOptions(httpContext));
        httpContext.Response.Cookies.Delete(AiChatAuthDefaults.SessionCookieName, BuildDeleteCookieOptions(httpContext));
    }

    private static CookieOptions BuildCookieOptions(HttpContext httpContext, DateTimeOffset expiresAtUtc)
    {
        return new CookieOptions
        {
            Expires = expiresAtUtc.UtcDateTime,
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true
        };
    }

    private static CookieOptions BuildDeleteCookieOptions(HttpContext httpContext)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true
        };
    }

    private static TimeSpan ResolveTicketLifetime(IConfiguration configuration)
    {
        var configuredHours = configuration.GetValue<double?>("AiChatAuth:Hours");
        if (configuredHours is > 0)
        {
            return TimeSpan.FromHours(configuredHours.Value);
        }

        return AiChatAuthDefaults.TicketLifetime;
    }

    private static string HashUserAgent(HttpContext httpContext)
    {
        var userAgent = httpContext.Request.Headers.UserAgent.ToString().Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(userAgent));
        return Convert.ToHexString(bytes);
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record AiChatTicketPayload(
        string SessionId,
        string UserAgentHash,
        DateTimeOffset ExpiresAtUtc,
        string Nonce);
}
