using System.Text.Json;
using MatchPredictor.Web.Filters;
using MatchPredictor.Web.Middleware;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class PrivateSelectionsAuthTests
{
    private const string ExpectedPasskey = "mp-synergy-owner-2026";

    [Fact]
    public async Task AdminUsageBasicAuthMiddleware_ExemptsAdminSelections_BypassesBasicAuth()
    {
        var nextCalled = false;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UsageDashboard:Username"] = "admin",
                ["UsageDashboard:Password"] = "secret"
            })
            .Build();

        var middleware = new AdminUsageBasicAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            configuration,
            new StubAdminUiTicketService(),
            NullLogger<AdminUsageBasicAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/selections";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminUsageBasicAuthMiddleware_ExemptsAdminOperations_BypassesBasicAuth()
    {
        var nextCalled = false;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UsageDashboard:Username"] = "admin",
                ["UsageDashboard:Password"] = "secret"
            })
            .Build();

        var middleware = new AdminUsageBasicAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            configuration,
            new StubAdminUiTicketService(),
            NullLogger<AdminUsageBasicAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/operations";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task PrivateSelectionsAuthAttribute_WhenNoPasskeyOrCookie_ReturnsNotFound()
    {
        var (attribute, filterContext) = CreateFilterContext();

        await attribute.OnAuthorizationAsync(filterContext);

        Assert.IsType<NotFoundResult>(filterContext.Result);
    }

    [Fact]
    public async Task PrivateSelectionsAuthAttribute_WhenInvalidPasskeySupplied_ReturnsNotFound()
    {
        var (attribute, filterContext) = CreateFilterContext();
        filterContext.HttpContext.Request.QueryString = new QueryString("?key=invalid-passkey");

        await attribute.OnAuthorizationAsync(filterContext);

        Assert.IsType<NotFoundResult>(filterContext.Result);
    }

    [Fact]
    public async Task PrivateSelectionsAuthAttribute_WhenValidPasskeySupplied_SetsCookieAndRedirects()
    {
        var (attribute, filterContext) = CreateFilterContext();
        filterContext.HttpContext.Request.Path = "/Admin/Selections";
        filterContext.HttpContext.Request.QueryString = new QueryString($"?key={ExpectedPasskey}");

        await attribute.OnAuthorizationAsync(filterContext);

        var redirectResult = Assert.IsType<RedirectResult>(filterContext.Result);
        Assert.Equal("/Admin/Selections", redirectResult.Url);

        // Verify cookie was set in response
        var setCookieHeaders = filterContext.HttpContext.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains(PrivateSelectionsAuthAttribute.CookieName, setCookieHeaders);
    }

    [Fact]
    public async Task PrivateSelectionsAuthAttribute_WhenValidCookieSupplied_AllowsRequest()
    {
        var (attribute, filterContext) = CreateFilterContext();

        // Generate valid protected ticket
        var dataProtection = filterContext.HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
        var protector = dataProtection.CreateProtector("MatchPredictor.Web.PrivateSelections.AuthTicket.v1");
        var expiresAtUtc = DateTimeOffset.UtcNow.AddDays(30);
        var payload = JsonSerializer.Serialize(new { ExpiresAtUtc = expiresAtUtc, Nonce = Guid.NewGuid().ToString("N") });
        var ticket = protector.Protect(payload);

        filterContext.HttpContext.Request.Headers.Cookie = $"{PrivateSelectionsAuthAttribute.CookieName}={ticket}";

        await attribute.OnAuthorizationAsync(filterContext);

        // Result remains null, meaning the request is authorized and pipeline proceeds
        Assert.Null(filterContext.Result);
    }

    [Fact]
    public async Task PrivateSelectionsAuthAttribute_WhenCookieTampered_ReturnsNotFound()
    {
        var (attribute, filterContext) = CreateFilterContext();
        filterContext.HttpContext.Request.Headers.Cookie = $"{PrivateSelectionsAuthAttribute.CookieName}=corrupted-tampered-token";

        await attribute.OnAuthorizationAsync(filterContext);

        Assert.IsType<NotFoundResult>(filterContext.Result);
    }

    private static (PrivateSelectionsAuthAttribute Attribute, AuthorizationFilterContext Context) CreateFilterContext()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminSettings:SelectionsPasskey"] = ExpectedPasskey
            })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };
        httpContext.Request.Path = "/Admin/Selections";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        return (new PrivateSelectionsAuthAttribute(), filterContext);
    }

    private sealed class StubAdminUiTicketService : IAdminUiTicketService
    {
        public bool Authenticated => false;
        public void SignIn(HttpContext httpContext) { }
        public bool IsAuthenticated(HttpContext httpContext) => false;
    }
}
