using System.Text.Json;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public interface IAiChatSessionStore
{
    Task<AiChatSessionState> LoadAsync(string sessionId, CancellationToken ct);

    Task SaveTurnAsync(
        string sessionId,
        AiChatSessionState state,
        string userPrompt,
        AiChatResponse response,
        AiChatContextBuilder.AiChatContextSelection? selection,
        IReadOnlyCollection<int> discussedPredictionIds,
        AiChatNormalizedRequest? normalizedRequest,
        CancellationToken ct,
        string? knowledgeTopic = null);
}

public sealed class AiChatSessionStore : IAiChatSessionStore
{
    private const int MaxHistoryItems = 12;
    private const int MaxMessageLength = 1000;
    private static readonly TimeSpan SessionSlidingExpiration = TimeSpan.FromHours(12);

    private readonly IDistributedCache _cache;
    private readonly ILogger<AiChatSessionStore> _logger;

    public AiChatSessionStore(IDistributedCache cache, ILogger<AiChatSessionStore> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<AiChatSessionState> LoadAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new AiChatSessionState();
        }

        var cacheValue = await _cache.GetStringAsync(GetSessionCacheKey(sessionId), ct);
        if (string.IsNullOrWhiteSpace(cacheValue))
        {
            return new AiChatSessionState();
        }

        try
        {
            return JsonSerializer.Deserialize<AiChatSessionState>(cacheValue, JsonOptions()) ?? new AiChatSessionState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize AI chat session state for session {SessionId}. Resetting state.", sessionId);
            return new AiChatSessionState();
        }
    }

    public async Task SaveTurnAsync(
        string sessionId,
        AiChatSessionState state,
        string userPrompt,
        AiChatResponse response,
        AiChatContextBuilder.AiChatContextSelection? selection,
        IReadOnlyCollection<int> discussedPredictionIds,
        AiChatNormalizedRequest? normalizedRequest,
        CancellationToken ct,
        string? knowledgeTopic = null)
    {
        state.History.Add(new ChatHistoryItem { Role = "user", Content = NormalizeHistoryContent(userPrompt) });
        state.History.Add(new ChatHistoryItem { Role = "assistant", Content = NormalizeHistoryContent(response.Message) });
        state.History = state.History
            .TakeLast(MaxHistoryItems)
            .ToList();
        state.LastRecommendedActionKeys = response.Actions
            .Select(action => action.ActionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.LastContextPredictionIds = selection?.Candidates
            .Select(candidate => candidate.PredictionId)
            .Distinct()
            .ToList() ?? [];
        state.LastDiscussedPredictionIds = discussedPredictionIds
            .Distinct()
            .ToList();
        state.LastIntent = response.ContextMode;
        state.LastKnowledgeTopic = knowledgeTopic ?? state.LastKnowledgeTopic;
        state.LastNormalizedRequest = normalizedRequest;
        state.LastResolvedMarketMix = selection?.ResolvedMarketMix.ToList() ?? [];
        state.LastShortfallWarnings = selection?.ShortfallWarnings.ToList() ?? [];

        if (response.Actions.Count > 0 &&
            (response.ContextMode == "recommend_picks" ||
             response.ContextMode == "mixed_market_recommendation" ||
             response.ContextMode == "working_slip_refinement"))
        {
            state.WorkingSlipActionKeys = response.Actions
                .Select(action => action.ActionKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            state.WorkingSlipPredictionIds = response.Actions
                .Select(action => action.PredictionId)
                .Distinct()
                .ToList();
        }

        var payload = JsonSerializer.Serialize(state);
        await _cache.SetStringAsync(
            GetSessionCacheKey(sessionId),
            payload,
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = SessionSlidingExpiration
            },
            ct);
    }

    private static string NormalizeHistoryContent(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length <= MaxMessageLength)
        {
            return normalized;
        }

        return normalized[..MaxMessageLength];
    }

    private static string GetSessionCacheKey(string sessionId) => $"ai-chat-session:{sessionId}";

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
