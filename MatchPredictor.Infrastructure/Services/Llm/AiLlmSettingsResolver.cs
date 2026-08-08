using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Infrastructure.Services.Llm;

public sealed record ResolvedAiLlmSettings
{
    public required string Provider { get; init; }
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public required string BaseUrl { get; init; }
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Optional second provider tried when the primary call fails.</summary>
    public ResolvedAiLlmSettings? Fallback { get; init; }

    public bool HasValidKey =>
        !string.IsNullOrWhiteSpace(ApiKey) && !IsPlaceholderKey(ApiKey);

    public bool IsConfigured => HasValidKey || (Fallback?.HasValidKey ?? false);

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
    public const string OpenAiProvider = "openai";

    public const string DefaultGeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
    public const string DefaultGeminiModel = "gemini-3.5-flash";
    public const string DefaultGroqBaseUrl = "https://api.groq.com/openai/v1";
    public const string DefaultGroqModel = "openai/gpt-oss-120b";
    public const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";
    public const string DefaultOpenAiModel = "gpt-5.6-luna";

    private readonly IConfiguration _configuration;

    public AiLlmSettingsResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ResolvedAiLlmSettings Resolve()
    {
        var section = _configuration.GetSection(AiLlmOptions.SectionName);
        var configuredProvider = NormalizeProvider(section["Provider"] ?? _configuration["AiLlm:Provider"]);
        var timeout = ResolveTimeout(section);

        var primaryKey = ResolvePrimaryApiKey(configuredProvider, section);
        string provider;
        string apiKey;

        if (!string.IsNullOrEmpty(primaryKey))
        {
            provider = configuredProvider;
            apiKey = primaryKey;
        }
        else
        {
            var groqKey = CoalesceKey(_configuration["GroqApiKey"]);
            if (!string.IsNullOrEmpty(groqKey) &&
                !string.Equals(configuredProvider, OpenAiProvider, StringComparison.Ordinal))
            {
                // Legacy deployments: only GroqApiKey is set → use Groq (unless explicitly targeting OpenAI).
                provider = GroqProvider;
                apiKey = groqKey;
            }
            else
            {
                provider = configuredProvider;
                apiKey = string.Empty;
            }
        }

        var primary = BuildEndpointSettings(provider, apiKey, section, timeout, isFallback: false);
        var fallback = ResolveFallbackSettings(section, primary.Provider, timeout);

        return primary with { Fallback = fallback };
    }

    public static (string BaseUrl, string Model) GetPreset(string provider) =>
        provider switch
        {
            GroqProvider => (DefaultGroqBaseUrl, DefaultGroqModel),
            OpenAiProvider => (DefaultOpenAiBaseUrl, DefaultOpenAiModel),
            _ => (DefaultGeminiBaseUrl, DefaultGeminiModel)
        };

    public static string BuildChatCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed + "/chat/completions";
    }

    private ResolvedAiLlmSettings? ResolveFallbackSettings(
        IConfigurationSection section,
        string primaryProvider,
        int timeout)
    {
        var fallbackSection = section.GetSection("Fallback");
        var explicitFallbackKey = CoalesceKey(
            fallbackSection["ApiKey"],
            _configuration["AiLlm:Fallback:ApiKey"]);

        string? fallbackKey;
        string fallbackProvider;

        if (string.Equals(primaryProvider, OpenAiProvider, StringComparison.Ordinal))
        {
            // When primary is OpenAI, Gemini can be supplied via Fallback:* or GEMINI_API_KEY.
            fallbackKey = CoalesceKey(explicitFallbackKey, _configuration["GEMINI_API_KEY"]);
            fallbackProvider = NormalizeProvider(
                fallbackSection["Provider"]
                ?? _configuration["AiLlm:Fallback:Provider"]
                ?? GeminiProvider);
        }
        else if (!string.IsNullOrEmpty(explicitFallbackKey))
        {
            fallbackKey = explicitFallbackKey;
            fallbackProvider = NormalizeProvider(
                fallbackSection["Provider"]
                ?? _configuration["AiLlm:Fallback:Provider"]
                ?? GeminiProvider);
        }
        else
        {
            return null;
        }

        if (string.IsNullOrEmpty(fallbackKey))
        {
            return null;
        }

        // Avoid a useless identical retry against the same provider+key.
        if (string.Equals(fallbackProvider, primaryProvider, StringComparison.Ordinal) &&
            string.Equals(
                fallbackKey,
                CoalesceKey(section["ApiKey"], _configuration["AiLlm:ApiKey"]),
                StringComparison.Ordinal))
        {
            return null;
        }

        return BuildEndpointSettings(fallbackProvider, fallbackKey, fallbackSection, timeout, isFallback: true);
    }

    private ResolvedAiLlmSettings BuildEndpointSettings(
        string provider,
        string apiKey,
        IConfigurationSection endpointSection,
        int timeout,
        bool isFallback)
    {
        var (presetBaseUrl, presetModel) = GetPreset(provider);
        var groqModel = CoalesceKey(_configuration["GroqModel"]);

        string? configuredModel;
        string? configuredBaseUrl;
        if (isFallback)
        {
            configuredModel = CoalesceKey(
                endpointSection["Model"],
                _configuration["AiLlm:Fallback:Model"]);
            configuredBaseUrl = CoalesceKey(
                endpointSection["BaseUrl"],
                _configuration["AiLlm:Fallback:BaseUrl"]);
        }
        else
        {
            configuredModel = CoalesceKey(endpointSection["Model"], _configuration["AiLlm:Model"]);
            configuredBaseUrl = CoalesceKey(endpointSection["BaseUrl"], _configuration["AiLlm:BaseUrl"]);
        }

        var model = configuredModel
                    ?? (string.Equals(provider, GroqProvider, StringComparison.Ordinal) ? groqModel : null)
                    ?? presetModel;
        var baseUrl = configuredBaseUrl ?? presetBaseUrl;

        var endpointTimeoutRaw = endpointSection["TimeoutSeconds"];
        var endpointTimeout = int.TryParse(endpointTimeoutRaw, out var parsed) && parsed > 0
            ? parsed
            : timeout;

        return new ResolvedAiLlmSettings
        {
            Provider = provider,
            ApiKey = apiKey,
            Model = model,
            BaseUrl = NormalizeBaseUrl(baseUrl),
            TimeoutSeconds = endpointTimeout
        };
    }

    private string? ResolvePrimaryApiKey(string provider, IConfigurationSection section)
    {
        if (string.Equals(provider, OpenAiProvider, StringComparison.Ordinal))
        {
            // Do not treat GEMINI_API_KEY as an OpenAI key.
            return CoalesceKey(section["ApiKey"], _configuration["AiLlm:ApiKey"]);
        }

        if (string.Equals(provider, GroqProvider, StringComparison.Ordinal))
        {
            return CoalesceKey(section["ApiKey"], _configuration["AiLlm:ApiKey"], _configuration["GroqApiKey"]);
        }

        return CoalesceKey(section["ApiKey"], _configuration["AiLlm:ApiKey"], _configuration["GEMINI_API_KEY"]);
    }

    private int ResolveTimeout(IConfigurationSection section)
    {
        var timeoutRaw = section["TimeoutSeconds"] ?? _configuration["AiLlm:TimeoutSeconds"];
        return int.TryParse(timeoutRaw, out var parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : 60;
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
            "openai" or "chatgpt" => OpenAiProvider,
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
