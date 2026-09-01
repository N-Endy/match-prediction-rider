using System.Net;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class WebApplicationFactorySmokeTests
{
    [Fact]
    public async Task PublicPredictionPages_ReturnSuccess_InTestHost()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var bttsResponse = await client.GetAsync("/predictions/btts");
        var drawResponse = await client.GetAsync("/predictions/draw");

        bttsResponse.EnsureSuccessStatusCode();
        drawResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task OgPreview_ReturnsPng()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var response = await client.GetAsync("/og-preview.png");

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task HomePage_IncludesOgImageMetaTag()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("property=\"og:image\" content=\"https://localhost/og-preview.png\"", html);
        Assert.Contains("property=\"og:image:secure_url\" content=\"https://localhost/og-preview.png\"", html);
        Assert.Contains("property=\"og:image:type\" content=\"image/png\"", html);
        Assert.Contains("name=\"twitter:card\" content=\"summary_large_image\"", html);
    }

    [Fact]
    public async Task Manifest_ReturnsWebManifestJson()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var response = await client.GetAsync("/manifest.webmanifest");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"name\": \"MatchPredictor\"", body);
        Assert.Contains("\"display\": \"standalone\"", body);
        Assert.Contains("/icon-192.png", body);
    }

    [Fact]
    public async Task ServiceWorker_ReturnsJavaScriptWithNoCache()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var response = await client.GetAsync("/service-worker.js");

        response.EnsureSuccessStatusCode();
        Assert.StartsWith("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("matchpredictor-v1", body);
    }

    [Fact]
    public async Task OfflinePage_ReturnsHtml()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var response = await client.GetAsync("/offline.html");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("You're offline", body);
    }

    [Fact]
    public async Task HomePage_IncludesPwaMetaTags()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("rel=\"manifest\"", html);
        Assert.Contains("manifest.webmanifest", html);
        Assert.Contains("name=\"theme-color\" content=\"#0a0e17\"", html);
        Assert.Contains("apple-mobile-web-app-capable", html);
        Assert.Contains("pwaInstallBanner", html);
    }

    [Fact]
    public async Task Analytics_ReturnsUnauthorized_WithoutBasicAuth()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var response = await client.GetAsync("/analytics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.WwwAuthenticate.Any());
    }

    [Fact]
    public async Task ValueBets_ReturnsTooManyRequests_WhenRateLimitExceeded()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        HttpResponseMessage? lastResponse = null;
        for (var i = 0; i < 11; i++)
        {
            lastResponse = await client.GetAsync("/api/valuebets");
        }

        Assert.NotNull(lastResponse);
        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=matchpredictor_test;Username=test;Password=test",
                        ["RUN_BACKGROUND_JOBS"] = "false",
                        ["ENABLE_USER_TRACKING"] = "false",
                        ["SKIP_STARTUP_INITIALIZATION"] = "true",
                        ["Hangfire:Username"] = "test",
                        ["Hangfire:Password"] = "test"
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPredictionQueries>();
                    services.AddScoped<IPredictionQueries, StubPredictionQueries>();
                    services.RemoveAll<IValueBetsService>();
                    services.AddScoped<IValueBetsService, StubValueBetsService>();
                });
            });
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    private sealed class StubPredictionQueries : IPredictionQueries
    {
        public Task<IReadOnlyList<Prediction>> GetBTTSAsync(DateTime date) => Empty();

        public Task<IReadOnlyList<Prediction>> GetOver25Async(DateTime date) => Empty();

        public Task<IReadOnlyList<Prediction>> GetUnder25Async(DateTime date) => Empty();

        public Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date) => Empty();

        public Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date) => Empty();

        private static Task<IReadOnlyList<Prediction>> Empty()
        {
            return Task.FromResult<IReadOnlyList<Prediction>>([]);
        }
    }

    private sealed class StubValueBetsService : IValueBetsService
    {
        public Task<IEnumerable<ValueBetDto>> GetTopValueBetsAsync(int limit = 60, CancellationToken ct = default)
        {
            return Task.FromResult(Enumerable.Empty<ValueBetDto>());
        }

        public Task<ValueBetReportDto> GetValueBetReportAsync(int limit = 60, CancellationToken ct = default)
        {
            return Task.FromResult(new ValueBetReportDto
            {
                GeneratedAtLocal = DateTime.UtcNow
            });
        }
    }
}
