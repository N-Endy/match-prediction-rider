using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services.Llm;

public sealed class OpenAiCompatibleChatCompletionsClient : IChatCompletionsClient
{
    public const string HttpClientName = "AiLlm";
    public const int WebSearchTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAiLlmSettingsResolver _settingsResolver;
    private readonly ILogger<OpenAiCompatibleChatCompletionsClient> _logger;

    public OpenAiCompatibleChatCompletionsClient(
        IHttpClientFactory httpClientFactory,
        IAiLlmSettingsResolver settingsResolver,
        ILogger<OpenAiCompatibleChatCompletionsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settingsResolver = settingsResolver;
        _logger = logger;
    }

    public bool IsConfigured => _settingsResolver.Resolve().IsConfigured;

    public string Provider
    {
        get
        {
            var settings = _settingsResolver.Resolve();
            return EffectivePrimary(settings).Provider;
        }
    }

    public string Model
    {
        get
        {
            var settings = _settingsResolver.Resolve();
            return EffectivePrimary(settings).Model;
        }
    }

    public async Task<ChatCompletionsResult> CompleteAsync(
        ChatCompletionsRequest request,
        CancellationToken ct = default)
    {
        var settings = _settingsResolver.Resolve();
        if (!settings.IsConfigured)
        {
            return new ChatCompletionsResult
            {
                Success = false,
                ExceptionMessage = "AI API key is not configured."
            };
        }

        var primary = settings.HasValidKey ? settings : null;
        var fallback = settings.Fallback is { HasValidKey: true } ? settings.Fallback : null;

        if (primary is null && fallback is null)
        {
            return new ChatCompletionsResult
            {
                Success = false,
                ExceptionMessage = "AI API key is not configured."
            };
        }

        if (primary is null)
        {
            return await CompleteWithSettingsAsync(fallback!, request, ct);
        }

        var primaryResult = await CompleteWithSettingsAsync(primary, request, ct);
        if (IsUsableSuccess(primaryResult) || ct.IsCancellationRequested || fallback is null)
        {
            return primaryResult;
        }

        if (!ShouldFailover(primaryResult))
        {
            return primaryResult;
        }

        _logger.LogWarning(
            "Primary LLM provider {PrimaryProvider}/{PrimaryModel} failed (status={Status}, timeout={IsTimeout}, rateLimited={IsRateLimited}, error={Error}). Falling back to {FallbackProvider}/{FallbackModel}.",
            primary.Provider,
            primary.Model,
            primaryResult.StatusCode,
            primaryResult.IsTimeout,
            primaryResult.IsRateLimited,
            SummarizeFailure(primaryResult),
            fallback.Provider,
            fallback.Model);

        var fallbackRequest = request.UseWebSearch
            ? new ChatCompletionsRequest
            {
                Messages = request.Messages,
                JsonMode = true,
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens
            }
            : request;
        var fallbackResult = await CompleteWithSettingsAsync(fallback, fallbackRequest, ct);
        if (IsUsableSuccess(fallbackResult))
        {
            _logger.LogInformation(
                "Fallback LLM provider {FallbackProvider}/{FallbackModel} succeeded after primary failure.",
                fallback.Provider,
                fallback.Model);
        }

        return fallbackResult;
    }

    private async Task<ChatCompletionsResult> CompleteWithSettingsAsync(
        ResolvedAiLlmSettings settings,
        ChatCompletionsRequest request,
        CancellationToken ct)
    {
        try
        {
            var useResponses = request.UseWebSearch &&
                string.Equals(settings.Provider, AiLlmSettingsResolver.OpenAiProvider, StringComparison.Ordinal);
            var timeoutSeconds = request.TimeoutSeconds ?? settings.TimeoutSeconds;
            if (useResponses)
            {
                timeoutSeconds = Math.Max(timeoutSeconds, WebSearchTimeoutSeconds);
            }

            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            if (httpClient.Timeout == Timeout.InfiniteTimeSpan || httpClient.Timeout.TotalSeconds < timeoutSeconds)
            {
                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            var url = useResponses
                ? AiLlmSettingsResolver.BuildResponsesUrl(settings.BaseUrl)
                : AiLlmSettingsResolver.BuildChatCompletionsUrl(settings.BaseUrl);
            var body = useResponses
                ? BuildResponsesRequestBody(settings, request)
                : BuildRequestBody(settings, request);
            var json = JsonSerializer.Serialize(body, SerializerOptions);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "Calling LLM provider {LlmProvider} model {LlmModel} at {LlmUrl}",
                settings.Provider,
                settings.Model,
                url);

            using var response = await httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "LLM provider {LlmProvider} API error: {Status} {Body}",
                    settings.Provider,
                    response.StatusCode,
                    responseBody[..Math.Min(300, responseBody.Length)]);

                return new ChatCompletionsResult
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    IsRateLimited = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests,
                    ErrorBody = responseBody
                };
            }

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                content = useResponses
                    ? ExtractResponsesContent(doc.RootElement)
                    : ExtractChatCompletionsContent(doc.RootElement);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "LLM provider {LlmProvider} returned invalid JSON", settings.Provider);
                return new ChatCompletionsResult
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    ExceptionMessage = "Invalid JSON response from LLM provider.",
                    ErrorBody = responseBody
                };
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return new ChatCompletionsResult
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    ExceptionMessage = "Empty content from LLM provider.",
                    ErrorBody = responseBody
                };
            }

            return new ChatCompletionsResult
            {
                Success = true,
                StatusCode = (int)response.StatusCode,
                Content = content
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new ChatCompletionsResult
            {
                Success = false,
                IsTimeout = true,
                ExceptionMessage = "Request timed out."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling {Provider} API", settings.Provider);
            return new ChatCompletionsResult
            {
                Success = false,
                ExceptionMessage = ex.Message
            };
        }
    }

    private static ResolvedAiLlmSettings EffectivePrimary(ResolvedAiLlmSettings settings) =>
        settings.HasValidKey
            ? settings
            : settings.Fallback is { HasValidKey: true } fallback
                ? fallback
                : settings;

    private static bool IsUsableSuccess(ChatCompletionsResult result) =>
        result.Success && !string.IsNullOrWhiteSpace(result.Content);

    private static bool ShouldFailover(ChatCompletionsResult result) =>
        !IsUsableSuccess(result);

    private static string SummarizeFailure(ChatCompletionsResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ExceptionMessage))
        {
            return result.ExceptionMessage;
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorBody))
        {
            return result.ErrorBody[..Math.Min(120, result.ErrorBody.Length)];
        }

        return "unknown";
    }

    private static Dictionary<string, object?> BuildRequestBody(
        ResolvedAiLlmSettings settings,
        ChatCompletionsRequest request)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["messages"] = request.Messages
                .Select(m => new { role = m.Role, content = m.Content })
                .ToArray()
        };

        if (request.JsonMode)
        {
            body["response_format"] = new { type = "json_object" };
        }

        var isGemini = string.Equals(settings.Provider, AiLlmSettingsResolver.GeminiProvider, StringComparison.Ordinal);
        var isOpenAi = string.Equals(settings.Provider, AiLlmSettingsResolver.OpenAiProvider, StringComparison.Ordinal);

        // Gemini rejects custom sampling; GPT-5.x Luna only accepts the default temperature.
        if (request.Temperature is { } temperature && !isGemini && !isOpenAi)
        {
            body["temperature"] = temperature;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            // GPT-5.x OpenAI models prefer max_completion_tokens.
            if (isOpenAi)
            {
                body["max_completion_tokens"] = maxTokens;
            }
            else
            {
                body["max_tokens"] = maxTokens;
            }
        }

        var reasoningEffort = request.ReasoningEffort;
        if (string.IsNullOrWhiteSpace(reasoningEffort) && isGemini && request.JsonMode)
        {
            // Prefer low-latency JSON for structured MatchPredictor calls.
            reasoningEffort = "none";
        }

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            body["reasoning_effort"] = reasoningEffort;
        }

        return body;
    }

    private static Dictionary<string, object?> BuildResponsesRequestBody(
        ResolvedAiLlmSettings settings,
        ChatCompletionsRequest request)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["input"] = request.Messages
                .Select(m => new { role = m.Role, content = m.Content })
                .ToArray(),
            ["tools"] = new object[] { new { type = "web_search" } },
            ["tool_choice"] = "auto"
        };

        if (request.MaxTokens is { } maxTokens)
        {
            body["max_output_tokens"] = maxTokens;
        }

        return body;
    }

    private static string? ExtractChatCompletionsContent(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString();
        }

        return null;
    }

    private string? ExtractResponsesContent(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(outputText.GetString()))
        {
            LogWebSearchCalls(root);
            return outputText.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return ExtractChatCompletionsContent(root);
        }

        var searchCalls = 0;
        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

            if (string.Equals(type, "web_search_call", StringComparison.OrdinalIgnoreCase))
            {
                searchCalls++;
                continue;
            }

            if (!string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = ExtractMessageText(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        if (searchCalls > 0)
        {
            _logger.LogInformation("Luna web search completed {SearchCallCount} search call(s) for banker ranking.", searchCalls);
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private void LogWebSearchCalls(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var searchCalls = output.EnumerateArray().Count(item =>
            item.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(typeElement.GetString(), "web_search_call", StringComparison.OrdinalIgnoreCase));
        if (searchCalls > 0)
        {
            _logger.LogInformation("Luna web search completed {SearchCallCount} search call(s) for banker ranking.", searchCalls);
        }
    }

    private static string? ExtractMessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                var value = part.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value);
                }

                continue;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var propertyName in new[] { "text", "output_text" })
            {
                if (part.TryGetProperty(propertyName, out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(textElement.GetString()))
                {
                    parts.Add(textElement.GetString()!);
                    break;
                }
            }
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }
}
