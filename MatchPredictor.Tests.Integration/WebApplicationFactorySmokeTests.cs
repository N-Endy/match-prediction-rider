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
    public async Task HomePage_DoesNotIncludeAnalyticsNavLink_ForPublicUsers()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.DoesNotContain("href=\"/analytics\"", html);
        Assert.Contains("href=\"/about\"", html);
        Assert.Contains("href=\"/results\"", html);
    }

    [Fact]
    public async Task TrustAndResultsPages_ReturnSuccess_WithExpectedHeadings()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var about = await client.GetStringAsync("/about");
        var methodology = await client.GetStringAsync("/methodology");
        var faq = await client.GetStringAsync("/faq");
        var terms = await client.GetStringAsync("/terms");
        var contact = await client.GetStringAsync("/contact");
        var results = await client.GetStringAsync("/results");

        Assert.Contains("About MatchPredictor", about);
        Assert.Contains("How MatchPredictor builds the daily card", methodology);
        Assert.Contains("Frequently asked questions", faq);
        Assert.Contains("Terms of Use", terms);
        Assert.Contains("Contact MatchPredictor", contact);
        Assert.Contains("Settled published results", results);
    }

    [Fact]
    public async Task PredictionPages_IncludeUniqueMarketExplainers()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var btts = await client.GetStringAsync("/predictions/btts");
        var over = await client.GetStringAsync("/predictions/over2");
        var under = await client.GetStringAsync("/predictions/under2");
        var win = await client.GetStringAsync("/predictions/straightwin");
        var draw = await client.GetStringAsync("/predictions/draw");

        Assert.Contains("How Both Teams to Score picks are built", btts);
        Assert.Contains("How Over 2.5 Goals picks are built", over);
        Assert.Contains("How Under 2.5 Goals picks are built", under);
        Assert.Contains("How Straight Win picks are built", win);
        Assert.Contains("How Draw picks are built", draw);
    }

    [Fact]
    public async Task SitemapAndRobots_IncludePublicUrls()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var sitemap = await client.GetStringAsync("/sitemap.xml");
        var robots = await client.GetStringAsync("/robots.txt");

        Assert.Contains("https://matchpredictor.dev/about", sitemap);
        Assert.Contains("https://matchpredictor.dev/results", sitemap);
        Assert.Contains("https://matchpredictor.dev/methodology", sitemap);
        Assert.Contains("Sitemap: https://matchpredictor.dev/sitemap.xml", robots);
    }

    [Fact]
    public async Task HomePage_UsesNonPersonalizedCookieBannerCopy()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("non-personalized ads", html);
        Assert.DoesNotContain("serve targeted ads", html);
    }

    [Fact]
    public async Task CanonicalUrl_DoesNotIncludeQueryString()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var html = await client.GetStringAsync("/predictions/btts?league=Test");

        Assert.Contains("rel=\"canonical\" href=\"https://localhost/predictions/btts\"", html);
        Assert.DoesNotContain("rel=\"canonical\" href=\"https://localhost/predictions/btts?league=Test\"", html);
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
        Assert.Contains("matchpredictor-v2", body);
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
    public async Task HomePage_RequestsNonPersonalizedAds()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("requestNonPersonalizedAds = 1", html);
        Assert.Contains("data-npa=\"1\"", html);
    }

    [Fact]
    public async Task PrivacyPage_DisclosesAdServingAndNonPersonalizedAds()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var html = await client.GetStringAsync("/Privacy");

        Assert.Contains("policies.google.com/technologies/partner-sites", html);
        Assert.Contains("non-personalized", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("web beacons", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IP addresses", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ErrorPage_DoesNotRenderAdBanner()
    {
        await using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var html = await client.GetStringAsync("/Error");

        Assert.DoesNotContain("class=\"adsbygoogle", html);
        Assert.DoesNotContain("data-ad-slot=", html);
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

        public Task<IReadOnlyList<Prediction>> GetRecentSettledPublishedAsync(int days = 30) => Empty();

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
