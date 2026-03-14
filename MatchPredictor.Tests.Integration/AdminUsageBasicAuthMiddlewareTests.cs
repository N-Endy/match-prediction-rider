using System.Text;
using MatchPredictor.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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
    public async Task InvokeAsync_ForProtectedPathWithValidCredentials_AllowsRequest()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(() => nextCalled = true);
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/usage";
        context.Request.Headers.Authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("usage-admin:super-secret"))}";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static AdminUsageBasicAuthMiddleware CreateMiddleware(Action? onNext = null)
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
            configuration);
    }
}
