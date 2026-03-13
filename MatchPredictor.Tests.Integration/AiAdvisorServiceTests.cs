using System.Net;
using System.Text;
using System.Text.Json;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiAdvisorServiceTests
{
    [Fact]
    public async Task GetAdviceAsync_DiscardsUnknownActionKeys_AndSuppressesBookAllForSingleValidAction()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 2);
        var validActionKey = $"P{predictions[0].Id}";

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {
                  "message": "Here is the safest angle on today's card.",
                  "recommendations": [
                    { "actionKey": "P999999", "explanation": "Ignore this one." },
                    { "actionKey": "P999998", "explanation": "Ignore this too." },
                    { "actionKey": "P1", "explanation": "BTTS clears the line with strong confidence." },
                    { "actionKey": "P1", "explanation": "Duplicate." }
                  ],
                  "showBookAll": true,
                  "warnings": ["Grounded to today's published card."]
                }
                """.Replace("\"P1\"", $"\"{validActionKey}\"")));

        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("Give me the best BTTS predictions", "session-1");

        Assert.Equal("Here is the safest angle on today's card.", response.Message);
        var action = Assert.Single(response.Actions);
        Assert.Equal(validActionKey, action.ActionKey);
        Assert.Equal("BTTS clears the line with strong confidence.", action.Explanation);
        Assert.False(response.ShowBookAll);
        Assert.Single(response.Warnings);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_ReturnsRawFallbackMessage_WhenModelDoesNotReturnJson()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(context, 1);

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("This is not valid JSON."));
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("Give me the best BTTS predictions", "session-2");

        Assert.Equal("This is not valid JSON.", response.Message);
        Assert.Empty(response.Actions);
        Assert.False(response.ShowBookAll);
    }

    [Fact]
    public async Task GetAdviceAsync_UsesSavedRecommendations_ForBookingFollowUpWithoutCallingAiAgain()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 2);

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "These are the two best options on today's card.",
                  "recommendedActionKeys": ["P{{predictions[0].Id}}", "P{{predictions[1].Id}}"],
                  "showBookAll": true
                }
                """));

        var service = CreateService(context, handler);

        var firstResponse = await service.GetAdviceAsync("Give me 2 strong picks", "session-3");
        var followUpResponse = await service.GetAdviceAsync("Book them", "session-3");

        Assert.Equal(2, firstResponse.Actions.Count);
        Assert.Equal(2, followUpResponse.Actions.Count);
        Assert.True(followUpResponse.ShowBookAll);
        Assert.Contains("lined up", followUpResponse.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_TrimsSessionHistoryToLastSixTurns()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(context, 1);

        var handler = new SequenceHttpMessageHandler(
            Enumerable.Range(1, 7)
                .Select(index => BuildGroqResponse($$"""
                    {
                      "message": "Reply {{index}}",
                      "recommendedActionKeys": [],
                      "showBookAll": false
                    }
                    """))
                .ToArray());

        var cache = new TestDistributedCache();
        var service = CreateService(context, handler, cache);

        for (var index = 1; index <= 7; index++)
        {
            await service.GetAdviceAsync($"Give me pick {index}", "session-4");
        }

        var storedStateJson = cache.GetStoredString("ai-chat-session:session-4");
        Assert.NotNull(storedStateJson);

        var state = JsonSerializer.Deserialize<AiChatSessionState>(storedStateJson!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(state);
        Assert.Equal(12, state!.History.Count);
        Assert.DoesNotContain(state.History, item => item.Content == "Give me pick 1");
        Assert.DoesNotContain(state.History, item => item.Content == "Reply 1");
        Assert.Contains(state.History, item => item.Content == "Give me pick 7");
        Assert.Contains(state.History, item => item.Content == "Reply 7");
    }

    [Fact]
    public async Task GetAdviceAsync_ReturnsNoPredictionsMessage_WhenOnlyPastMatchesRemain()
    {
        await using var context = CreateContext();

        context.Predictions.Add(new Prediction
        {
            Date = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.GetLocalTime().AddHours(-2).ToString("HH:mm"),
            MatchDateTime = DateTime.UtcNow.AddMinutes(-30),
            League = "England - Premier League",
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ConfidenceScore = 0.71m,
            RawConfidenceScore = 0.70m,
            ThresholdUsed = 0.55,
            ThresholdSource = "Configured",
            CalibratorUsed = "Bucket",
            WasPublished = true
        });

        await context.SaveChangesAsync();

        var handler = new SequenceHttpMessageHandler();
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("What are the best BTTS picks?", "session-5");

        Assert.Contains("No predictions are available for today", response.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_FillsRequestedMarketSlices_AndEnablesBookAll_ForLargeMixedBookingRequest()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(
            context,
            Enumerable.Range(1, 25).Select(index => ("BothTeamsScore", "BTTS", 0.86m - (index * 0.004m), 0.55d, $"BTTS Home {index}", $"BTTS Away {index}", "England - Premier League"))
                .Concat(Enumerable.Range(1, 25).Select(index => ("Over2.5Goals", "Over 2.5", 0.84m - (index * 0.004m), 0.58d, $"Over Home {index}", $"Over Away {index}", "Italy - Serie A")))
                .Concat(Enumerable.Range(1, 25).Select(index => ("StraightWin", "Home Win", 0.82m - (index * 0.004m), 0.68d, $"Straight Home {index}", $"Straight Away {index}", "Spain - La Liga"))));

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {
                  "message": "I've lined up the strongest mix from today's card.",
                  "recommendedActionKeys": ["P999999"],
                  "showBookAll": false
                }
                """));

        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync(
            "Give me 20 btts, 20 over and 20 straightwin and book them",
            "session-6");

        Assert.Equal("I've lined up the strongest mix from today's card.", response.Message);
        Assert.Equal(60, response.Actions.Count);
        Assert.Equal(20, response.Actions.Count(action => action.Market == "BTTS"));
        Assert.Equal(20, response.Actions.Count(action => action.Market == "Over2.5"));
        Assert.Equal(20, response.Actions.Count(action => action.Market == "1X2"));
        Assert.All(response.Actions, action => Assert.False(string.IsNullOrWhiteSpace(action.Explanation)));
        Assert.True(response.ShowBookAll);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_MapsPerActionExplanations_FromStructuredRecommendations()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 1);

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here is the best fit for that fixture.",
                  "recommendations": [
                    {
                      "actionKey": "P{{predictions[0].Id}}",
                      "explanation": "This pick stays on today's card and clears its threshold with room to spare."
                    }
                  ],
                  "showBookAll": false
                }
                """));

        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("Tell me about Beta 1 vs Delta 1", "session-7");

        var action = Assert.Single(response.Actions);
        Assert.Equal($"P{predictions[0].Id}", action.ActionKey);
        Assert.Equal("This pick stays on today's card and clears its threshold with room to spare.", action.Explanation);
    }

    private static async Task<List<Prediction>> SeedPredictionsAsync(ApplicationDbContext context, int count)
    {
        var localNow = DateTimeProvider.GetLocalTime();
        var date = localNow.ToString("dd-MM-yyyy");
        var kickoffTime = localNow.AddMinutes(30).ToString("HH:mm");

        var predictions = Enumerable.Range(1, count)
            .Select(index => new Prediction
            {
                Date = date,
                Time = kickoffTime,
                MatchDateTime = null,
                League = index % 2 == 0 ? "England - Premier League" : "Italy - Serie A",
                HomeTeam = index % 2 == 0 ? $"Alpha {index}" : $"Beta {index}",
                AwayTeam = index % 2 == 0 ? $"Gamma {index}" : $"Delta {index}",
                PredictionCategory = index % 2 == 0 ? "StraightWin" : "BothTeamsScore",
                PredictedOutcome = index % 2 == 0 ? "Home Win" : "BTTS",
                ConfidenceScore = 0.75m - (index * 0.02m),
                RawConfidenceScore = 0.73m - (index * 0.02m),
                ThresholdUsed = index % 2 == 0 ? 0.68 : 0.55,
                ThresholdSource = "Configured",
                CalibratorUsed = "Bucket",
                WasPublished = true
            })
            .ToList();

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();
        return predictions;
    }

    private static async Task<List<Prediction>> SeedPredictionsAsync(
        ApplicationDbContext context,
        IEnumerable<(string Category, string Outcome, decimal Confidence, double ThresholdUsed, string HomeTeam, string AwayTeam, string League)> specs)
    {
        var localNow = DateTimeProvider.GetLocalTime();
        var date = localNow.ToString("dd-MM-yyyy");
        var kickoffTime = localNow.AddMinutes(30).ToString("HH:mm");

        var predictions = specs
            .Select((spec, index) => new Prediction
            {
                Date = date,
                Time = kickoffTime,
                MatchDateTime = null,
                League = spec.League,
                HomeTeam = spec.HomeTeam,
                AwayTeam = spec.AwayTeam,
                PredictionCategory = spec.Category,
                PredictedOutcome = spec.Outcome,
                ConfidenceScore = spec.Confidence,
                RawConfidenceScore = spec.Confidence - 0.01m,
                ThresholdUsed = spec.ThresholdUsed,
                ThresholdSource = "Configured",
                CalibratorUsed = "Bucket",
                WasPublished = true
            })
            .ToList();

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();
        return predictions;
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }

    private static AiAdvisorService CreateService(
        ApplicationDbContext context,
        SequenceHttpMessageHandler handler,
        TestDistributedCache? cache = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GroqApiKey"] = "test-api-key",
                ["GroqModel"] = "test-model"
            })
            .Build();

        return new AiAdvisorService(
            context,
            configuration,
            NullLogger<AiAdvisorService>.Instance,
            new StubHttpClientFactory(handler),
            cache ?? new TestDistributedCache());
    }

    private static string BuildGroqResponse(string modelContent)
    {
        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = modelContent
                    }
                }
            }
        });
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public SequenceHttpMessageHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Unexpected HTTP call with no queued response.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TestDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

        public byte[]? Get(string key) => _entries.TryGetValue(key, out var value) ? value : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _entries.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _entries[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public string? GetStoredString(string key)
        {
            return _entries.TryGetValue(key, out var value)
                ? Encoding.UTF8.GetString(value)
                : null;
        }
    }
}
