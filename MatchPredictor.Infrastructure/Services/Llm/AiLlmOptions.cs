namespace MatchPredictor.Infrastructure.Services.Llm;

public sealed class AiLlmOptions
{
    public const string SectionName = "AiLlm";

    /// <summary>Provider preset: "gemini" (default) or "groq".</summary>
    public string Provider { get; set; } = "gemini";

    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    /// <summary>OpenAI-compatible base URL (without trailing chat/completions).</summary>
    public string? BaseUrl { get; set; }

    public int TimeoutSeconds { get; set; } = 60;
}
