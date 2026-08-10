using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MatchPredictor.Web.Services;

public static class AdminOperatorKeys
{
    public const string HttpContextItem = "IsAdminOperator";
    public const string AuthCookieName = "MP_ADMIN_UI";
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(12);
}

public interface IAdminUiTicketService
{
    void SignIn(HttpContext httpContext);
    bool IsAuthenticated(HttpContext httpContext);
}

public sealed class AdminUiTicketService : IAdminUiTicketService
{
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ticketLifetime;

    public AdminUiTicketService(
        IDataProtectionProvider dataProtectionProvider,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("MatchPredictor.Web.AdminUi.AuthTicket.v1");
        _timeProvider = timeProvider;
        _ticketLifetime = ResolveTicketLifetime(configuration);
    }

    public void SignIn(HttpContext httpContext)
    {
        var now = _timeProvider.GetUtcNow();
        var expiresAtUtc = now.Add(_ticketLifetime);
        var payload = new AdminUiTicketPayload(expiresAtUtc, Guid.NewGuid().ToString("N"));
        var protectedTicket = _protector.Protect(JsonSerializer.Serialize(payload));

        httpContext.Response.Cookies.Append(
            AdminOperatorKeys.AuthCookieName,
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

    public bool IsAuthenticated(HttpContext httpContext)
    {
        var protectedTicket = httpContext.Request.Cookies[AdminOperatorKeys.AuthCookieName];
        if (string.IsNullOrWhiteSpace(protectedTicket))
        {
            return false;
        }

        try
        {
            var json = _protector.Unprotect(protectedTicket);
            var payload = JsonSerializer.Deserialize<AdminUiTicketPayload>(json);
            return payload is not null && payload.ExpiresAtUtc > _timeProvider.GetUtcNow();
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan ResolveTicketLifetime(IConfiguration configuration)
    {
        var configuredHours = configuration.GetValue<double?>("AdminUiAuth:Hours");
        if (configuredHours is > 0)
        {
            return TimeSpan.FromHours(configuredHours.Value);
        }

        return AdminOperatorKeys.TicketLifetime;
    }

    private sealed record AdminUiTicketPayload(DateTimeOffset ExpiresAtUtc, string Nonce);
}

public static class AdminOperatorHttpContextExtensions
{
    public static bool IsAdminOperator(this HttpContext? httpContext) =>
        httpContext?.Items[AdminOperatorKeys.HttpContextItem] is true;
}
