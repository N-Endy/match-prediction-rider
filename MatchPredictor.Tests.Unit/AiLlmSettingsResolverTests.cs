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
        Assert.Null(settings.Fallback);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            AiLlmSettingsResolver.BuildChatCompletionsUrl(settings.BaseUrl));
    }

    [Fact]
    public void Resolve_OpenAiPreset_UsesLunaDefaults()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["AiLlm:ApiKey"] = "sk-test"
        }).Resolve();

        Assert.Equal(AiLlmSettingsResolver.OpenAiProvider, settings.Provider);
        Assert.Equal("sk-test", settings.ApiKey);
        Assert.Equal(AiLlmSettingsResolver.DefaultOpenAiModel, settings.Model);
        Assert.Equal("https://api.openai.com/v1/", settings.BaseUrl);
        Assert.True(settings.IsConfigured);
        Assert.Null(settings.Fallback);
        Assert.Equal(
            "https://api.openai.com/v1/chat/completions",
            AiLlmSettingsResolver.BuildChatCompletionsUrl(settings.BaseUrl));
    }

    [Fact]
    public void Resolve_OpenAi_UsesGeminiFallback_FromNestedSection()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "chatgpt",
            ["AiLlm:ApiKey"] = "sk-primary",
            ["AiLlm:Fallback:Provider"] = "gemini",
            ["AiLlm:Fallback:ApiKey"] = "gemini-fallback",
            ["AiLlm:Fallback:Model"] = "gemini-3.5-flash"
        }).Resolve();

        Assert.Equal(AiLlmSettingsResolver.OpenAiProvider, settings.Provider);
        Assert.True(settings.HasValidKey);
        Assert.NotNull(settings.Fallback);
        Assert.Equal(AiLlmSettingsResolver.GeminiProvider, settings.Fallback!.Provider);
        Assert.Equal("gemini-fallback", settings.Fallback.ApiKey);
        Assert.Equal(AiLlmSettingsResolver.DefaultGeminiModel, settings.Fallback.Model);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/", settings.Fallback.BaseUrl);
    }

    [Fact]
    public void Resolve_OpenAi_UsesGeminiApiKeyEnv_AsFallback()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["AiLlm:ApiKey"] = "sk-primary",
            ["GEMINI_API_KEY"] = "gemini-from-env"
        }).Resolve();

        Assert.Equal("sk-primary", settings.ApiKey);
        Assert.NotNull(settings.Fallback);
        Assert.Equal("gemini-from-env", settings.Fallback!.ApiKey);
        Assert.Equal(AiLlmSettingsResolver.GeminiProvider, settings.Fallback.Provider);
    }

    [Fact]
    public void Resolve_OpenAi_WithoutOpenAiKey_UsesGeminiFallbackOnly()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["GEMINI_API_KEY"] = "gemini-only"
        }).Resolve();

        Assert.Equal(AiLlmSettingsResolver.OpenAiProvider, settings.Provider);
        Assert.False(settings.HasValidKey);
        Assert.True(settings.IsConfigured);
        Assert.NotNull(settings.Fallback);
        Assert.Equal("gemini-only", settings.Fallback!.ApiKey);
    }

    [Fact]
    public void Resolve_OpenAi_DoesNotTreatGeminiEnvAsPrimaryKey()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["GEMINI_API_KEY"] = "gemini-env"
        }).Resolve();

        Assert.NotEqual("gemini-env", settings.ApiKey);
        Assert.Equal("gemini-env", settings.Fallback!.ApiKey);
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
            ["AiLlm:Model"] = "gemini-3.5-flash",
            ["AiLlm:BaseUrl"] = "https://custom.example/openai",
            ["AiLlm:TimeoutSeconds"] = "90"
        }).Resolve();

        Assert.Equal("alias-key", settings.ApiKey);
        Assert.Equal("gemini-3.5-flash", settings.Model);
        Assert.Equal("https://custom.example/openai/", settings.BaseUrl);
        Assert.Equal(90, settings.TimeoutSeconds);
    }

    [Fact]
    public void Resolve_IgnoresPlaceholderKeys()
    {
        var settings = CreateResolver(new Dictionary<string, string?>
        {
            ["AiLlm:ApiKey"] = "(stored in user-secrets)",
            ["GroqApiKey"] = "(set via environment variable)",
            ["AiLlm:Fallback:ApiKey"] = "(stored in user-secrets)"
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
            ["AiLlm:Model"] = "gemini-3.5-flash"
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
        Assert.Equal("gemini-3.5-flash", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
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

    [Fact]
    public async Task CompleteAsync_OpenAi_UsesMaxCompletionTokens()
    {
        string? requestBody = null;

        var handler = new CapturingHandler((request, _) =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(CreateOpenAiResponse("luna-ok"));
        });

        var client = CreateClient(handler, new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["AiLlm:ApiKey"] = "sk-test",
            ["AiLlm:Model"] = "gpt-5.6-luna"
        });

        var result = await client.CompleteAsync(new ChatCompletionsRequest
        {
            Temperature = 0.3,
            MaxTokens = 50,
            Messages = [new ChatCompletionsMessage { Role = "user", Content = "hi" }]
        });

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(requestBody!);
        Assert.Equal(50, doc.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task CompleteAsync_PrimaryFailure_RetriesFallback()
    {
        var callCount = 0;
        var hosts = new List<string>();

        var handler = new CapturingHandler((request, _) =>
        {
            callCount++;
            hosts.Add(request.RequestUri!.Host);
            if (callCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("boom", Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(CreateOpenAiResponse("from-gemini"));
        });

        var client = CreateClient(handler, new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["AiLlm:ApiKey"] = "sk-test",
            ["AiLlm:Fallback:ApiKey"] = "gemini-key"
        });

        var result = await client.CompleteAsync(new ChatCompletionsRequest
        {
            Messages = [new ChatCompletionsMessage { Role = "user", Content = "hi" }]
        });

        Assert.True(result.Success);
        Assert.Equal("from-gemini", result.Content);
        Assert.Equal(2, callCount);
        Assert.Equal("api.openai.com", hosts[0]);
        Assert.Equal("generativelanguage.googleapis.com", hosts[1]);
    }

    [Fact]
    public async Task CompleteAsync_PrimarySuccess_DoesNotCallFallback()
    {
        var callCount = 0;

        var handler = new CapturingHandler((request, _) =>
        {
            callCount++;
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
            return Task.FromResult(CreateOpenAiResponse("from-luna"));
        });

        var client = CreateClient(handler, new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["AiLlm:ApiKey"] = "sk-test",
            ["AiLlm:Fallback:ApiKey"] = "gemini-key"
        });

        var result = await client.CompleteAsync(new ChatCompletionsRequest
        {
            Messages = [new ChatCompletionsMessage { Role = "user", Content = "hi" }]
        });

        Assert.True(result.Success);
        Assert.Equal("from-luna", result.Content);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task CompleteAsync_PrimaryEmptyContent_RetriesFallback()
    {
        var callCount = 0;

        var handler = new CapturingHandler((_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult(CreateOpenAiResponse("   "));
            }

            return Task.FromResult(CreateOpenAiResponse("fallback-content"));
        });

        var client = CreateClient(handler, new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["AiLlm:ApiKey"] = "sk-test",
            ["GEMINI_API_KEY"] = "gemini-key"
        });

        var result = await client.CompleteAsync(new ChatCompletionsRequest
        {
            Messages = [new ChatCompletionsMessage { Role = "user", Content = "hi" }]
        });

        Assert.True(result.Success);
        Assert.Equal("fallback-content", result.Content);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task CompleteAsync_BothFail_ReturnsFailure()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited", Encoding.UTF8, "text/plain")
            }));

        var client = CreateClient(handler, new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["AiLlm:ApiKey"] = "sk-test",
            ["AiLlm:Fallback:ApiKey"] = "gemini-key"
        });

        var result = await client.CompleteAsync(new ChatCompletionsRequest
        {
            Messages = [new ChatCompletionsMessage { Role = "user", Content = "hi" }]
        });

        Assert.False(result.Success);
        Assert.True(result.IsRateLimited);
        Assert.Equal(429, result.StatusCode);
    }

    [Fact]
    public async Task CompleteAsync_OpenAiUnconfigured_UsesFallbackDirectly()
    {
        var callCount = 0;

        var handler = new CapturingHandler((request, _) =>
        {
            callCount++;
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri!.Host);
            return Task.FromResult(CreateOpenAiResponse("gemini-only"));
        });

        var client = CreateClient(handler, new Dictionary<string, string?>
        {
            ["AiLlm:Provider"] = "openai",
            ["GEMINI_API_KEY"] = "gemini-key"
        });

        Assert.True(client.IsConfigured);
        Assert.Equal(AiLlmSettingsResolver.GeminiProvider, client.Provider);

        var result = await client.CompleteAsync(new ChatCompletionsRequest
        {
            Messages = [new ChatCompletionsMessage { Role = "user", Content = "hi" }]
        });

        Assert.True(result.Success);
        Assert.Equal("gemini-only", result.Content);
        Assert.Equal(1, callCount);
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
