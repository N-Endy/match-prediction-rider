namespace MatchPredictor.Infrastructure.Services.Llm;

public sealed class ChatCompletionsMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public sealed class ChatCompletionsRequest
{
    public required IReadOnlyList<ChatCompletionsMessage> Messages { get; init; }
    public bool JsonMode { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// When true and the provider is OpenAI, call the Responses API with hosted web_search.
    /// Ignored for Gemini/Groq.
    /// </summary>
    public bool UseWebSearch { get; init; }

    /// <summary>
    /// Optional OpenAI-compat reasoning_effort (e.g. "none" for Gemini latency-sensitive calls).
    /// </summary>
    public string? ReasoningEffort { get; init; }
}

public sealed class ChatCompletionsResult
{
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public int? StatusCode { get; init; }
    public bool IsRateLimited { get; init; }
    public bool IsTimeout { get; init; }
    public string? ErrorBody { get; init; }
    public string? ExceptionMessage { get; init; }
}
