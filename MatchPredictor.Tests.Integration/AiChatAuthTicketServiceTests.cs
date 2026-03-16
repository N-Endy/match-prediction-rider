using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiChatAuthTicketServiceTests
{
    [Fact]
    public void SignIn_IssuesTicketThatValidates()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 03, 16, 8, 0, 0, TimeSpan.Zero));
        var service = CreateService(timeProvider);
        var signInContext = CreateContext("Mozilla/5.0");

        var issuedSessionId = service.SignIn(signInContext);
        var requestContext = CreateFollowUpContext(signInContext, "Mozilla/5.0");

        var isValid = service.TryValidate(requestContext, out var validatedSessionId);

        Assert.True(isValid);
        Assert.Equal(issuedSessionId, validatedSessionId);
    }

    [Fact]
    public void TryValidate_WithTamperedTicket_ReturnsFalse()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 03, 16, 8, 0, 0, TimeSpan.Zero));
        var service = CreateService(timeProvider);
        var signInContext = CreateContext("Mozilla/5.0");
        service.SignIn(signInContext);

        var requestContext = CreateFollowUpContext(signInContext, "Mozilla/5.0");
        requestContext.Request.Headers.Cookie = requestContext.Request.Headers.Cookie
            .ToString()
            .Replace($"{AiChatAuthDefaults.AuthCookieName}=", $"{AiChatAuthDefaults.AuthCookieName}=tampered-", StringComparison.Ordinal);

        var isValid = service.TryValidate(requestContext, out _);
        var responseCookies = requestContext.Response.Headers.SetCookie.ToArray();

        Assert.False(isValid);
        Assert.Contains(responseCookies, cookie => cookie.Contains($"{AiChatAuthDefaults.AuthCookieName}=;", StringComparison.Ordinal));
    }

    [Fact]
    public void TryValidate_ExpiredTicket_ReturnsFalse()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 03, 16, 8, 0, 0, TimeSpan.Zero));
        var service = CreateService(
            timeProvider,
            new Dictionary<string, string?> { ["AiChatAuth:Hours"] = "0.001" });
        var signInContext = CreateContext("Mozilla/5.0");
        service.SignIn(signInContext);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var requestContext = CreateFollowUpContext(signInContext, "Mozilla/5.0");

        var isValid = service.TryValidate(requestContext, out _);

        Assert.False(isValid);
    }

    [Fact]
    public void SignOut_DeletesAuthAndSessionCookies()
    {
        var service = CreateService(new TestTimeProvider(DateTimeOffset.UtcNow));
        var context = CreateContext("Mozilla/5.0");
        service.SignIn(context);

        var logoutContext = CreateFollowUpContext(context, "Mozilla/5.0");
        service.SignOut(logoutContext);
        var responseCookies = logoutContext.Response.Headers.SetCookie.ToArray();

        Assert.Contains(responseCookies, cookie => cookie.Contains($"{AiChatAuthDefaults.AuthCookieName}=;", StringComparison.Ordinal));
        Assert.Contains(responseCookies, cookie => cookie.Contains($"{AiChatAuthDefaults.SessionCookieName}=;", StringComparison.Ordinal));
    }

    private static IAiChatAuthTicketService CreateService(TimeProvider timeProvider, IDictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        return new AiChatAuthTicketService(
            DataProtectionProvider.Create("matchpredictor-tests"),
            configuration,
            timeProvider);
    }

    private static DefaultHttpContext CreateContext(string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        return context;
    }

    private static DefaultHttpContext CreateFollowUpContext(DefaultHttpContext sourceContext, string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        var responseCookies = sourceContext.Response.Headers.SetCookie.ToArray();
        context.Request.Headers.Cookie = string.Join(
            "; ",
            responseCookies.Select(cookie => cookie.Split(';', 2)[0]));
        return context;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
