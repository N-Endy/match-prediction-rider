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
    public const string DefaultGeminiModel = "gemini-3.8-flash";
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
        var explicitProvider = section["Provider"] ?? _configuration["AiLlm:Provider"];
        var explicitModel = section["Model"] ?? _configuration["AiLlm:Model"];
        var timeout = ResolveTimeout(section);

        var configuredKey = CoalesceKey(section["ApiKey"], _configuration["AiLlm:ApiKey"]);
        var geminiKey = CoalesceKey(_configuration["GEMINI_API_KEY"]);
        var groqKey = CoalesceKey(_configuration["GroqApiKey"]);

        var detectedFromConfiguredKey = DetectProviderFromKey(configuredKey);

        string provider;
        string apiKey;

        if (detectedFromConfiguredKey != null)
        {
            provider = detectedFromConfiguredKey;
            apiKey = configuredKey!;
        }
        else if (!string.IsNullOrEmpty(geminiKey) &&
                 (string.IsNullOrEmpty(explicitProvider) || string.Equals(NormalizeProvider(explicitProvider), GeminiProvider, StringComparison.Ordinal)))
        {
            provider = GeminiProvider;
            apiKey = geminiKey;
        }
        else
        {
            var initialProvider = ResolveProvider(explicitProvider, explicitModel, configuredKey);
            var primaryKey = ResolvePrimaryApiKey(initialProvider, section);
            if (!string.IsNullOrEmpty(primaryKey))
            {
                provider = ResolveProvider(explicitProvider, explicitModel, primaryKey);
                apiKey = primaryKey;
            }
            else if (!string.IsNullOrEmpty(groqKey) && !string.Equals(initialProvider, OpenAiProvider, StringComparison.Ordinal))
            {
                provider = GroqProvider;
                apiKey = groqKey;
            }
            else
            {
                provider = initialProvider;
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

    public static string BuildResponsesUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed + "/responses";
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

        var explicitFallbackProvider = fallbackSection["Provider"] ?? _configuration["AiLlm:Fallback:Provider"];
        var explicitFallbackModel = fallbackSection["Model"] ?? _configuration["AiLlm:Fallback:Model"];

        string? fallbackKey;
        string fallbackProvider;

        if (string.Equals(primaryProvider, OpenAiProvider, StringComparison.Ordinal))
        {
            // When primary is OpenAI, Gemini can be supplied via Fallback:* or GEMINI_API_KEY.
            fallbackKey = CoalesceKey(explicitFallbackKey, _configuration["GEMINI_API_KEY"], _configuration["GroqApiKey"]);
            fallbackProvider = ResolveProvider(explicitFallbackProvider, explicitFallbackModel, fallbackKey, defaultProvider: GeminiProvider);
        }
        else if (string.Equals(primaryProvider, GeminiProvider, StringComparison.Ordinal))
        {
            // When primary is Gemini, OpenAI can be supplied via Fallback:*, or Groq via GroqApiKey.
            var groqKey = CoalesceKey(_configuration["GroqApiKey"]);
            fallbackKey = CoalesceKey(explicitFallbackKey, groqKey);
            var defaultFallback = groqKey != null && string.IsNullOrEmpty(explicitFallbackKey)
                ? GroqProvider
                : OpenAiProvider;
            fallbackProvider = ResolveProvider(explicitFallbackProvider, explicitFallbackModel, fallbackKey, defaultProvider: defaultFallback);
        }
        else if (!string.IsNullOrEmpty(explicitFallbackKey))
        {
            fallbackKey = explicitFallbackKey;
            fallbackProvider = ResolveProvider(explicitFallbackProvider, explicitFallbackModel, fallbackKey, defaultProvider: GeminiProvider);
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
                CoalesceKey(section["ApiKey"], _configuration["AiLlm:ApiKey"], _configuration["GEMINI_API_KEY"]),
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

        // Sanitize cross-provider BaseUrl contamination:
        // If configuredBaseUrl is explicitly pointing to another provider's endpoint, reject it and use presetBaseUrl.
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl) && IsCrossProviderBaseUrl(provider, configuredBaseUrl))
        {
            configuredBaseUrl = null;
        }

        var rawModel = configuredModel
                    ?? (string.Equals(provider, GroqProvider, StringComparison.Ordinal) ? groqModel : null)
                    ?? presetModel;
        var model = NormalizeDeprecatedModel(provider, rawModel);
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

    public static bool IsCrossProviderBaseUrl(string provider, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        var lower = baseUrl.ToLowerInvariant();
        if (string.Equals(provider, GeminiProvider, StringComparison.Ordinal))
        {
            return lower.Contains("api.openai.com") || lower.Contains("api.groq.com");
        }

        if (string.Equals(provider, OpenAiProvider, StringComparison.Ordinal))
        {
            return lower.Contains("googleapis.com") || lower.Contains("api.groq.com");
        }

        if (string.Equals(provider, GroqProvider, StringComparison.Ordinal))
        {
            return lower.Contains("googleapis.com") || lower.Contains("api.openai.com");
        }

        return false;
    }

    public static string? DetectProviderFromKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || ResolvedAiLlmSettings.IsPlaceholderKey(apiKey))
        {
            return null;
        }

        var key = apiKey.Trim();
        if (key.StartsWith("AIzaSy", StringComparison.Ordinal))
        {
            return GeminiProvider;
        }

        if (key.StartsWith("gsk_", StringComparison.OrdinalIgnoreCase))
        {
            return GroqProvider;
        }

        if (key.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAiProvider;
        }

        return null;
    }

    public static string NormalizeDeprecatedModel(string provider, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        if (string.Equals(provider, GeminiProvider, StringComparison.OrdinalIgnoreCase))
        {
            var stripped = model.Trim().Replace("models/", "", StringComparison.OrdinalIgnoreCase);
            var lower = stripped.ToLowerInvariant();
            if (lower == "gemini-2.5-flash" || lower == "gemini-2.0-flash" || lower == "gemini-1.5-flash")
            {
                return "gemini-3.8-flash";
            }

            if (lower == "gemini-2.5-pro" || lower == "gemini-2.0-pro" || lower == "gemini-1.5-pro")
            {
                return "gemini-3.8-pro";
            }
        }

        return model;
    }

    private static string ResolveProvider(
        string? provider,
        string? model,
        string? apiKey = null,
        string defaultProvider = GeminiProvider)
    {
        var keyProvider = DetectProviderFromKey(apiKey);
        if (!string.IsNullOrWhiteSpace(keyProvider))
        {
            return keyProvider;
        }

        if (!string.IsNullOrWhiteSpace(provider))
        {
            return NormalizeProvider(provider);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            var lowerModel = model.Trim().ToLowerInvariant();
            if (lowerModel.Contains("gpt") || lowerModel.Contains("luna") || lowerModel.StartsWith("o1") || lowerModel.StartsWith("o3"))
            {
                return OpenAiProvider;
            }

            if (lowerModel.Contains("llama") || lowerModel.Contains("mixtral") || lowerModel.Contains("deepseek"))
            {
                return GroqProvider;
            }

            if (lowerModel.Contains("gemini"))
            {
                return GeminiProvider;
            }
        }

        return defaultProvider;
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
