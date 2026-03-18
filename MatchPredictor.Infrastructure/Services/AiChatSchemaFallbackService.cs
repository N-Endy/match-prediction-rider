using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public class AiChatSchemaFallbackService : IAiChatSchemaFallbackService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiChatSchemaFallbackService> _logger;

    public AiChatSchemaFallbackService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AiChatSchemaFallbackService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AiChatNormalizedRequest?> TryParseAsync(
        string userPrompt,
        AiChatNormalizedRequest deterministicRequest,
        CancellationToken ct = default)
    {
        var apiKey = _configuration["GroqApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Contains("stored in user-secrets", StringComparison.OrdinalIgnoreCase) ||
            apiKey.Contains("set via environment variable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var model = _configuration["GroqModel"] ?? "llama-3.3-70b-versatile";
        using var client = _httpClientFactory.CreateClient(nameof(AiChatSchemaFallbackService));
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var requestPayload = new
        {
            model,
            temperature = 0.0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
                        You convert MatchPredictor chat prompts into a strict request schema.
                        Only map requests the app can actually support.
                        Supported intents: RecommendPicks, MixedMarketRecommendation, WorkingSlipRefinement, MatchDiscussion, SettlementExplanation, AppHelp, ValueBetRequest.
                        Supported markets: BothTeamsScore, Over2.5Goals, Draw, StraightWin.
                        Return exactly one JSON object with keys:
                        intent, requestedMarkets, requestedTotalCount, scope, bookableOnly, wantsBooking, targetCombinedOdds, safetyBias, valueBias, referencedContextMode, actionDirective, entityTerms, interpretationNotes, needsSemanticFallback, flexibleMix.
                        requestedMarkets must be an array of objects with predictionCategory, count, explicitCount.
                        If unsupported or unclear, return an object that keeps the likely intent but leaves unsupported fields empty.
                        Never invent fixtures or bookmaker data.
                        """,
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        prompt = userPrompt,
                        deterministicRequest = new
                        {
                            intent = deterministicRequest.Intent.ToString(),
                            deterministicRequest.RequestedMarkets,
                            deterministicRequest.RequestedTotalCount,
                            deterministicRequest.Scope,
                            deterministicRequest.BookableOnly,
                            deterministicRequest.WantsBooking,
                            deterministicRequest.TargetCombinedOdds,
                            deterministicRequest.SafetyBias,
                            deterministicRequest.ValueBias,
                            deterministicRequest.ReferencedContextMode,
                            deterministicRequest.ActionDirective,
                            deterministicRequest.EntityTerms,
                            deterministicRequest.InterpretationNotes
                        }
                    })
                }
            }
        };

        using var response = await client.PostAsync(
            "https://api.groq.com/openai/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json"),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("AI chat schema fallback returned status code {StatusCode}.", response.StatusCode);
            return null;
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AiChatNormalizedRequest>(content, JsonOptions());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse AI chat schema fallback response.");
            return null;
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
