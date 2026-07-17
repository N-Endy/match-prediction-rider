using System.Net;
using System.Text;
using System.Text.Json;
using MatchPredictor.Infrastructure.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class AiLlmSettingsResolverTests
{
    [Fact]
    public void Resolve_DefaultsToGeminiPreset_WhenAiLlmKeyPresent()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:ApiKey"] = "gemini-key"
        }).Resolve();

        Assert.Equal(AiLlmSettingsResolver.GeminiProvider, settings.Provider);
        Assert.Equal("gemini-key", settings.ApiKey);
        Assert.Equal(AiLlmSettingsResolver.DefaultGeminiModel, settings.Model);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/", settings.BaseUrl);
        Assert.True(settings.IsConfigured);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            AiLlmSettingsResolver.BuildChatCompletionsUrl(settings.BaseUrl));
    }

    [Fact]
    public void Resolve_FallsBackToGroq_WhenOnlyGroqApiKeyConfigured()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "gemini",
            ["GroqApiKey"] = "groq-key",
            ["GroqModel"] = "custom-groq-model"
        }).Resolve();

        Assert.Equal(AiLlmSettingsResolver.GroqProvider, settings.Provider);
        Assert.Equal("groq-key", settings.ApiKey);
        Assert.Equal("custom-groq-model", settings.Model);
        Assert.Equal("https://api.groq.com/openai/v1/", settings.BaseUrl);
    }

    [Fact]
    public void Resolve_AcceptsGeminiApiKeyAlias_AndExplicitOverrides()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "gemini",
            ["GEMINI_API_KEY"] = "alias-key",
            ["AiLlm:Model"] = "gemini-2.5-flash",
            ["AiLlm:BaseUrl"] = "https://custom.example/openai",
            ["AiLlm:TimeoutSeconds"] = "90"
        }).Resolve();

        Assert.Equal("alias-key", settings.ApiKey);
        Assert.Equal("gemini-2.5-flash", settings.Model);
        Assert.Equal("https://custom.example/openai/", settings.BaseUrl);
        Assert.Equal(90, settings.TimeoutSeconds);
    }

    [Fact]
    public void Resolve_IgnoresPlaceholderKeys()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:ApiKey"] = "(stored in user-secrets)",
            ["GroqApiKey"] = "(set via environment variable)"
        }).Resolve();

        Assert.False(settings.IsConfigured);
    }

    private static AiLlmSettingsResolver CreateResolver(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new AiLlmSettingsResolver(configuration);
    }
}

public class OpenAiCompatibleChatCompletionsClientTests
{
    [Fact]
    public async Task CompleteAsync_GeminiJsonMode_SendsReasoningEffortNone_AndExtractsContent()
    {
        string? requestBody = null;
        Uri? requestUri = null;

        var handler = new CapturingHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(CreateOpenAiResponse("{\"ok\":true}"));
        });

        var client = CreateClient(handler, new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "gemini",
            ["AiLlm:ApiKey"] = "gemini-key",
            ["AiLlm:Model"] = "gemini-2.5-flash"
        });

        var result = await client.CompleteAsync(new ChatCompletionsRequest
        {
            JsonMode = true,
            Temperature = 0.2,
            MaxTokens = 100,
            Messages =
            [
                new ChatCompletionsMessage { Role = "system", Content = "sys" },
                new ChatCompletionsMessage { Role = "user", Content = "hi" }
            ]
        });

        Assert.True(result.Success);
        Assert.Equal("{\"ok\":true}", result.Content);
        Assert.NotNull(requestUri);
        Assert.EndsWith("/chat/completions", requestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("generativelanguage.googleapis.com", requestUri.Host, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(requestBody!);
        Assert.Equal("gemini-2.5-flash", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(0.2, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(100, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("none", doc.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task CompleteAsync_GroqPath_OmitsDefaultReasoningEffort()
    {
        string? requestBody = null;

        var handler = new CapturingHandler((request, _) =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(CreateOpenAiResponse("hello"));
        });

        var client = CreateClient(handler, new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "groq",
            ["AiLlm:ApiKey"] = "groq-key",
            ["AiLlm:Model"] = "openai/gpt-oss-120b",
            ["AiLlm:BaseUrl"] = "https://api.groq.com/openai/v1"
        });

        var result = await client.CompleteAsync(new ChatCompletionsRequest
        {
            JsonMode = true,
            Temperature = 0.5,
            Messages =
            [
                new ChatCompletionsMessage { Role = "user", Content = "hi" }
            ]
        });

        Assert.True(result.Success);
        Assert.Equal("hello", result.Content);

        using var doc = JsonDocument.Parse(requestBody!);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(0.5, doc.RootElement.GetProperty("temperature").GetDouble());
    }

    private static OpenAiCompatibleChatCompletionsClient CreateClient(
        HttpMessageHandler handler,
        Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        return new OpenAiCompatibleChatCompletionsClient(
            new StubHttpClientFactory(handler),
            new AiLlmSettingsResolver(configuration),
            NullLogger<OpenAiCompatibleChatCompletionsClient>.Instance);
    }

    private static HttpResponseMessage CreateOpenAiResponse(string content)
    {
        var payload = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content }
                }
            }
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
