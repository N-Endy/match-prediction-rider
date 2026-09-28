namespace MatchPredictor.Domain.Interfaces;

public enum ModelTier
{
    Fast,
    DeepReasoning
}

public interface IAiLlmRouter
{
    ModelTier DetermineTier(string userPrompt, string? intent = null);
    string ResolveModelForTier(ModelTier tier, string defaultModel);
}
