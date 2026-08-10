using System.Text;
using MatchPredictor.Web.Middleware;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AdminUsageBasicAuthMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ForProtectedPathWithoutCredentials_ReturnsChallenge()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/usage";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("Basic", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task InvokeAsync_ForProtectedPathWithValidCredentials_AllowsRequestAndSetsOperator()
    {
        var nextCalled = false;
        var ticket = new StubAdminUiTicketService();
        var middleware = CreateMiddleware(() => nextCalled = true, ticket);
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/usage";
        context.Request.Headers.Authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("usage-admin:super-secret"))}";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(context.Items[AdminOperatorKeys.HttpContextItem] as bool?);
        Assert.True(ticket.SignedIn);
    }

    [Fact]
    public async Task InvokeAsync_ForAdminApiWithoutCredentials_ReturnsChallenge()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/api/score-link/confirm";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ForPublicPathWithValidTicket_SetsOperatorWithoutChallenge()
    {
        var nextCalled = false;
        var ticket = new StubAdminUiTicketService { Authenticated = true };
        var middleware = CreateMiddleware(() => nextCalled = true, ticket);
        var context = new DefaultHttpContext();
        context.Request.Path = "/Predictions/BTTS";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(context.Items[AdminOperatorKeys.HttpContextItem] as bool?);
    }

    [Fact]
    public async Task InvokeAsync_WhenCredentialsAreMissing_ReturnsServiceUnavailable()
    {
        var middleware = new AdminUsageBasicAuthMiddleware(
            _ => Task.CompletedTask,
            new ConfigurationBuilder().Build(),
            new StubAdminUiTicketService(),
            NullLogger<AdminUsageBasicAuthMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/usage";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    private static AdminUsageBasicAuthMiddleware CreateMiddleware(
        Action? onNext = null,
        IAdminUiTicketService? ticketService = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UsageDashboard:Username"] = "usage-admin",
                ["UsageDashboard:Password"] = "super-secret"
            })
            .Build();

        return new AdminUsageBasicAuthMiddleware(
            context =>
            {
                onNext?.Invoke();
                return Task.CompletedTask;
            },
            configuration,
            ticketService ?? new StubAdminUiTicketService(),
            NullLogger<AdminUsageBasicAuthMiddleware>.Instance);
    }

    private sealed class StubAdminUiTicketService : IAdminUiTicketService
    {
        public bool Authenticated { get; set; }
        public bool SignedIn { get; private set; }

        public void SignIn(HttpContext httpContext) => SignedIn = true;

        public bool IsAuthenticated(HttpContext httpContext) => Authenticated;
    }
}
