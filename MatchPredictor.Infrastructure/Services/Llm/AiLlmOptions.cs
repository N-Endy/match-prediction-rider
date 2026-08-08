namespace MatchPredictor.Infrastructure.Services.Llm;

public sealed class AiLlmOptions
{
    public const string SectionName = "AiLlm";

    /// <summary>Provider preset: "openai" (Luna), "gemini", or "groq".</summary>
    public string Provider { get; set; } = "openai";

    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    /// <summary>OpenAI-compatible base URL (without trailing chat/completions).</summary>
    public string? BaseUrl { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Optional secondary provider used when the primary call fails.</summary>
    public AiLlmFallbackOptions Fallback { get; set; } = new();
}

public sealed class AiLlmFallbackOptions
{
    public string? Provider { get; set; }

    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    public string? BaseUrl { get; set; }

    public int TimeoutSeconds { get; set; }
}
