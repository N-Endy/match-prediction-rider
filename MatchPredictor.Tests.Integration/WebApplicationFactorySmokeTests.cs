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

        public Task<IReadOnlyList<Prediction>> GetCombinedSampleAsync(DateTime date, int count) => Empty();

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
