using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services.Llm;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public class AiChatSchemaFallbackService : IAiChatSchemaFallbackService
{
    private readonly IChatCompletionsClient _chatClient;
    private readonly ILogger<AiChatSchemaFallbackService> _logger;

    public AiChatSchemaFallbackService(
        IChatCompletionsClient chatClient,
        ILogger<AiChatSchemaFallbackService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<AiChatNormalizedRequest?> TryParseAsync(
        string userPrompt,
        AiChatNormalizedRequest deterministicRequest,
        CancellationToken ct = default)
    {
        if (!_chatClient.IsConfigured)
        {
            return null;
        }

        var result = await _chatClient.CompleteAsync(
            new ChatCompletionsRequest
            {
                Temperature = 0.0,
                JsonMode = true,
                ReasoningEffort = "none",
                MaxTokens = 800,
                Messages =
                [
                    new ChatCompletionsMessage
                    {
                        Role = "system",
                        Content = """
                            You convert MatchPredictor chat prompts into a strict request schema.
                            Only map requests the app can actually support.
                            Supported intents: RecommendPicks, MixedMarketRecommendation, WorkingSlipRefinement, MatchDiscussion, SettlementExplanation, AppHelp, ValueBetRequest.
                            Supported markets: BothTeamsScore, Over2.5Goals, Under2.5Goals, StraightWin, Draw.
                            Return exactly one JSON object with keys:
                            intent, requestedMarkets, requestedTotalCount, scope, bookableOnly, wantsBooking, targetCombinedOdds, safetyBias, valueBias, referencedContextMode, actionDirective, entityTerms, interpretationNotes, needsSemanticFallback, flexibleMix, randomSelection, requireSameFixtureMarkets, isCatalogListing.
                            requestedMarkets must be an array of objects with predictionCategory, count, explicitCount.
                            Catalog listing questions (which/show/list/marked as/find what exists) set isCatalogListing=true.
                            Same-fixture doubles (a match listed under every named market, e.g. both gg and over 2.5) set requireSameFixtureMarkets=true and list those markets without treating them as independent slip-mix counts.
                            Explicit mix slips like "3 gg and 2 over" or "mixture across markets" keep requireSameFixtureMarkets=false and use flexibleMix / counts instead.
                            Book / add to slip / open slip sets wantsBooking=true and bookableOnly=true.
                            If unsupported or unclear, return an object that keeps the likely intent but leaves unsupported fields empty.
                            Never invent fixtures or bookmaker data.
                            """
                    },
                    new ChatCompletionsMessage
                    {
                        Role = "user",
                        Content = JsonSerializer.Serialize(new
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
                                deterministicRequest.InterpretationNotes,
                                deterministicRequest.RandomSelection,
                                deterministicRequest.RequireSameFixtureMarkets,
                                deterministicRequest.IsCatalogListing,
                                deterministicRequest.FlexibleMix
                            }
                        })
                    }
                ]
            },
            ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            _logger.LogDebug(
                "AI chat schema fallback failed. Success={Success} Status={StatusCode}",
                result.Success,
                result.StatusCode);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiChatNormalizedRequest>(result.Content, JsonOptions());
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
