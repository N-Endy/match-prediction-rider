using MatchPredictor.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services.Llm;

public class AiLlmRouter : IAiLlmRouter
{
    private readonly ILogger<AiLlmRouter> _logger;

    public AiLlmRouter(ILogger<AiLlmRouter> logger)
    {
        _logger = logger;
    }

    public ModelTier DetermineTier(string userPrompt, string? intent = null)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return ModelTier.Fast;
        }

        var normalized = userPrompt.ToLowerInvariant();

        // Complex intents require Deep Reasoning
        if (intent != null)
        {
            if (intent.Equals("match_discussion", StringComparison.OrdinalIgnoreCase) ||
                intent.Equals("mixed_market_recommendation", StringComparison.OrdinalIgnoreCase) ||
                intent.Equals("working_slip_refinement", StringComparison.OrdinalIgnoreCase))
            {
                return ModelTier.DeepReasoning;
            }
        }

        // Tactical comparisons & multi-match synthesis
        if (normalized.Contains(" vs ", StringComparison.Ordinal) ||
            normalized.Contains("versus", StringComparison.Ordinal) ||
            normalized.Contains("compare", StringComparison.Ordinal) ||
            normalized.Contains("analyze", StringComparison.Ordinal) ||
            normalized.Contains("tactical", StringComparison.Ordinal) ||
            normalized.Contains("lineup", StringComparison.Ordinal) ||
            normalized.Contains("injury", StringComparison.Ordinal) ||
            normalized.Contains("odds movement", StringComparison.Ordinal) ||
            normalized.Contains("betslip", StringComparison.Ordinal) ||
            normalized.Contains("accumulator", StringComparison.Ordinal) ||
            normalized.Contains("rollover", StringComparison.Ordinal))
        {
            return ModelTier.DeepReasoning;
        }

        // Short questions, navigation, glossary definitions default to Fast tier
        return ModelTier.Fast;
    }

    public string ResolveModelForTier(ModelTier tier, string defaultModel)
    {
        if (string.IsNullOrWhiteSpace(defaultModel))
        {
            return tier == ModelTier.Fast ? "gemini-3.8-flash" : "gemini-3.8-pro";
        }

        var normalizedDefault = AiLlmSettingsResolver.NormalizeDeprecatedModel(AiLlmSettingsResolver.GeminiProvider, defaultModel);
        var lower = normalizedDefault.ToLowerInvariant();

        if (tier == ModelTier.Fast)
        {
            // If already on flash/mini, keep it
            if (lower.Contains("flash") || lower.Contains("mini") || lower.Contains("8b"))
            {
                return normalizedDefault;
            }

            // Downshift pro to flash if fast tier requested
            if (lower.Contains("pro"))
            {
                return normalizedDefault.Replace("pro", "flash", StringComparison.OrdinalIgnoreCase);
            }

            return normalizedDefault;
        }
        else // DeepReasoning
        {
            // If on flash, upshift to pro for deep reasoning
            if (lower.Contains("flash"))
            {
                return normalizedDefault.Replace("flash", "pro", StringComparison.OrdinalIgnoreCase);
            }

            return normalizedDefault;
        }
    }
}
