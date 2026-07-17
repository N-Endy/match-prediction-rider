using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services.Llm;

public sealed class OpenAiCompatibleChatCompletionsClient : IChatCompletionsClient
{
    public const string HttpClientName = "AiLlm";

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

    public string Provider => _settingsResolver.Resolve().Provider;

    public string Model => _settingsResolver.Resolve().Model;

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

        try
        {
            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            if (httpClient.Timeout == Timeout.InfiniteTimeSpan || httpClient.Timeout.TotalSeconds < settings.TimeoutSeconds)
            {
                httpClient.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            }

            var url = AiLlmSettingsResolver.BuildChatCompletionsUrl(settings.BaseUrl);
            var body = BuildRequestBody(settings, request);
            var json = JsonSerializer.Serialize(body, SerializerOptions);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "Calling {Provider} model {Model} at {Url}",
                settings.Provider,
                settings.Model,
                url);

            using var response = await httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "{Provider} API error: {Status} {Body}",
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

            using var doc = JsonDocument.Parse(responseBody);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return new ChatCompletionsResult
            {
                Success = true,
                StatusCode = (int)response.StatusCode,
                Content = content ?? string.Empty
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
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

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        var reasoningEffort = request.ReasoningEffort;
        if (string.IsNullOrWhiteSpace(reasoningEffort) &&
            string.Equals(settings.Provider, AiLlmSettingsResolver.GeminiProvider, StringComparison.Ordinal) &&
            request.JsonMode)
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
}
