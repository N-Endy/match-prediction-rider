using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class UserTrackingServiceTests
{
    [Fact]
    public async Task TrackPageViewAsync_CreatesVisitorSessionAndPageViewEvent()
    {
        await using var context = CreateContext();
        var service = new UserTrackingService(context, NullLogger<UserTrackingService>.Instance);
        var httpContext = CreateHttpContext("/predictions/btts");

        await service.TrackPageViewAsync(httpContext);

        var session = await context.VisitorSessions.SingleAsync();
        var activity = await context.UserActivityEvents.SingleAsync();

        Assert.False(string.IsNullOrWhiteSpace(session.VisitorId));
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
        Assert.Equal("/predictions/btts", session.LandingPath);
        Assert.Equal("page_view", activity.EventType);
        Assert.Equal("/predictions/btts", activity.Path);
        Assert.True(httpContext.Response.Headers.SetCookie.Count >= 2);

        var snapshot = await service.GetUsageSnapshotAsync();
        Assert.Equal(1, snapshot.UniqueVisitorsLast24Hours);
        Assert.Equal(1, snapshot.PageViewsLast7Days);
        Assert.Contains(snapshot.TopPagesLast7Days, page => page.Path == "/predictions/btts" && page.Count == 1);
    }

    [Fact]
    public async Task TrackEventAsync_ReusesVisitorCookiesAndCountsActions()
    {
        await using var context = CreateContext();
        var service = new UserTrackingService(context, NullLogger<UserTrackingService>.Instance);

        var firstRequest = CreateHttpContext("/predictions/over2");
        await service.TrackPageViewAsync(firstRequest);

        var secondRequest = CreateHttpContext("/predictions/over2", firstRequest.Response.Headers.SetCookie);
        await service.TrackEventAsync(
            secondRequest,
            "add_to_cart",
            "/predictions/over2",
            new Dictionary<string, string?>
            {
                ["market"] = "Over2.5"
            });

        var thirdRequest = CreateHttpContext("/betslip", secondRequest.Response.Headers.SetCookie);
        await service.TrackEventAsync(
            thirdRequest,
            "booking_success",
            "/betslip",
            new Dictionary<string, string?>
            {
                ["selectionCount"] = "3"
            });

        Assert.Equal(1, await context.VisitorSessions.CountAsync());
        Assert.Equal(3, await context.UserActivityEvents.CountAsync());

        var snapshot = await service.GetUsageSnapshotAsync();
        Assert.Equal(1, snapshot.CartAddsLast7Days);
        Assert.Equal(1, snapshot.SuccessfulBookingsLast7Days);
        Assert.Equal(1, snapshot.UniqueVisitorsLast7Days);
    }

    [Fact]
    public async Task TrackPageViewAsync_SkipsBotTraffic()
    {
        await using var context = CreateContext();
        var service = new UserTrackingService(context, NullLogger<UserTrackingService>.Instance);
        var httpContext = CreateHttpContext("/predictions/btts", userAgent: "Googlebot/2.1");

        await service.TrackPageViewAsync(httpContext);

        Assert.Equal(0, await context.VisitorSessions.CountAsync());
        Assert.Equal(0, await context.UserActivityEvents.CountAsync());
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }

    private static DefaultHttpContext CreateHttpContext(
        string path,
        Microsoft.Extensions.Primitives.StringValues setCookies = default,
        string userAgent = "Mozilla/5.0")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = path;
        httpContext.Request.Headers.Accept = "text/html";
        httpContext.Request.Headers.UserAgent = userAgent;

        if (setCookies.Count > 0)
        {
            httpContext.Request.Headers.Cookie = string.Join(
                "; ",
                setCookies
                    .Select(cookie => cookie.Split(';', 2)[0])
                    .Distinct(StringComparer.Ordinal));
        }

        return httpContext;
    }
}
