using MatchPredictor.Web.Services;

namespace MatchPredictor.Web.Middleware;

public class UserTrackingMiddleware
{
    private readonly RequestDelegate _next;

    public UserTrackingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IUserTrackingService userTrackingService)
    {
        var acceptsHtml = context.Request.Headers.Accept.ToString()
            .Contains("text/html", StringComparison.OrdinalIgnoreCase);
        var shouldPrimeTracking = HttpMethods.IsGet(context.Request.Method) && acceptsHtml;

        if (shouldPrimeTracking)
        {
            await userTrackingService.EnsureTrackingContextAsync(context, context.RequestAborted);
        }

        await _next(context);

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return;
        }

        if (context.Response.StatusCode >= 400)
        {
            return;
        }

        if (!acceptsHtml)
        {
            return;
        }

        await userTrackingService.TrackPageViewAsync(context, context.RequestAborted);
    }
}
