using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Infrastructure.Services.Llm;

public sealed class ResolvedAiLlmSettings
{
    public required string Provider { get; init; }
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public required string BaseUrl { get; init; }
    public int TimeoutSeconds { get; init; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !IsPlaceholderKey(ApiKey);

    internal static bool IsPlaceholderKey(string apiKey) =>
        apiKey.Contains("stored in user-secrets", StringComparison.OrdinalIgnoreCase) ||
        apiKey.Contains("set via environment variable", StringComparison.OrdinalIgnoreCase);
}

public interface IAiLlmSettingsResolver
{
    ResolvedAiLlmSettings Resolve();
}

public sealed class AiLlmSettingsResolver : IAiLlmSettingsResolver
{
    public const string GeminiProvider = "gemini";
    public const string GroqProvider = "groq";

    public const string DefaultGeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
    public const string DefaultGeminiModel = "gemini-2.5-flash";
    public const string DefaultGroqBaseUrl = "https://api.groq.com/openai/v1";
    public const string DefaultGroqModel = "openai/gpt-oss-120b";

    private readonly IConfiguration _configuration;

    public AiLlmSettingsResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ResolvedAiLlmSettings Resolve()
    {
        var section = _configuration.GetSection(AiLlmOptions.SectionName);
        var configuredProvider = NormalizeProvider(section["Provider"] ?? _configuration["AiLlm:Provider"]);
        var aiLlmKey = CoalesceKey(section["ApiKey"], _configuration["AiLlm:ApiKey"], _configuration["GEMINI_API_KEY"]);
        var groqKey = CoalesceKey(_configuration["GroqApiKey"]);

        string provider;
        string apiKey;

        if (!string.IsNullOrEmpty(aiLlmKey))
        {
            provider = configuredProvider;
            apiKey = aiLlmKey;
        }
        else if (!string.IsNullOrEmpty(groqKey))
        {
            // Legacy deployments: only GroqApiKey is set → use Groq regardless of default gemini preset.
            provider = GroqProvider;
            apiKey = groqKey;
        }
        else
        {
            provider = configuredProvider;
            apiKey = string.Empty;
        }

        var (presetBaseUrl, presetModel) = GetPreset(provider);
        var groqModel = CoalesceKey(_configuration["GroqModel"]);
        var configuredModel = CoalesceKey(section["Model"], _configuration["AiLlm:Model"]);
        var configuredBaseUrl = CoalesceKey(section["BaseUrl"], _configuration["AiLlm:BaseUrl"]);
        var model = configuredModel
                    ?? (string.Equals(provider, GroqProvider, StringComparison.Ordinal) ? groqModel : null)
                    ?? presetModel;
        var baseUrl = configuredBaseUrl ?? presetBaseUrl;
        var timeoutRaw = section["TimeoutSeconds"] ?? _configuration["AiLlm:TimeoutSeconds"];
        var timeout = int.TryParse(timeoutRaw, out var parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : 60;

        return new ResolvedAiLlmSettings
        {
            Provider = provider,
            ApiKey = apiKey,
            Model = model,
            BaseUrl = NormalizeBaseUrl(baseUrl),
            TimeoutSeconds = timeout
        };
    }

    public static (string BaseUrl, string Model) GetPreset(string provider) =>
        string.Equals(provider, GroqProvider, StringComparison.Ordinal)
            ? (DefaultGroqBaseUrl, DefaultGroqModel)
            : (DefaultGeminiBaseUrl, DefaultGeminiModel);

    public static string BuildChatCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed + "/chat/completions";
    }

    private static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return GeminiProvider;
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            "groq" => GroqProvider,
            "gemini" or "google" => GeminiProvider,
            var other => other
        };
    }

    private static string NormalizeBaseUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/";

    private static string? CoalesceKey(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && !ResolvedAiLlmSettings.IsPlaceholderKey(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
