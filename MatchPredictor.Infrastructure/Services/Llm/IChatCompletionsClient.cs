namespace MatchPredictor.Infrastructure.Services.Llm;

public interface IChatCompletionsClient
{
    bool IsConfigured { get; }

    /// <summary>Resolved provider id (gemini, groq, …) for logging and user messages.</summary>
    string Provider { get; }

    string Model { get; }

    Task<ChatCompletionsResult> CompleteAsync(ChatCompletionsRequest request, CancellationToken ct = default);
}
