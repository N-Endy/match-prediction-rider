using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// AI Advisor using Google Gemini with optimized token usage.
/// - Uses gemini-2.0-flash-lite (higher free-tier limits)
/// - Compresses prediction context into compact table format (~60% fewer tokens)
/// - Retry with exponential backoff on 429 rate-limit errors
/// </summary>
public class AiAdvisorService : IAiAdvisorService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiAdvisorService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // Cache the prediction summary per day to avoid rebuilding it every call
    private static string _cachedSummary = "";
    private static string _cachedDate = "";
    private static readonly object _cacheLock = new();

    public AiAdvisorService(
        ApplicationDbContext dbContext,
        IConfiguration configuration,
        ILogger<AiAdvisorService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetAdviceAsync(string userPrompt, CancellationToken ct = default)
    {
        var apiKey = _configuration["GeminiApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return "⚠️ Gemini API key is not configured. Please add 'GeminiApiKey' to appsettings.json.";

        var predictionContext = await GetOrBuildPredictionContextAsync(ct);
        if (string.IsNullOrEmpty(predictionContext))
            return "No predictions available for today yet. Predictions are updated daily — check back soon!";

        var systemPrompt = BuildSystemPrompt(predictionContext);
        var fullPrompt = systemPrompt + "\n\nUser: " + userPrompt;

        return await CallGeminiWithRetryAsync(apiKey, fullPrompt, ct);
    }

    /// <summary>
    /// Builds and caches a compact prediction summary for the current day.
    /// Only rebuilds if the date changes or the cache is empty.
    /// </summary>
    private async Task<string> GetOrBuildPredictionContextAsync(CancellationToken ct)
    {
        var todayStr = DateTime.UtcNow.Date.ToString("dd-MM-yyyy");

        lock (_cacheLock)
        {
            if (_cachedDate == todayStr && !string.IsNullOrEmpty(_cachedSummary))
                return _cachedSummary;
        }

        // Fetch today's predictions — limit to 25 for token efficiency
        var predictions = await _dbContext.Predictions
            .Where(p => p.Date == todayStr)
            .OrderBy(p => p.League)
            .ThenBy(p => p.Time)
            .Take(25)
            .ToListAsync(ct);

        if (predictions.Count == 0)
            return "";

        // Compact format: one short line per match (saves ~60% tokens vs verbose format)
        var sb = new StringBuilder();
        sb.AppendLine($"Today ({todayStr}), {predictions.Count} predictions:");

        var grouped = predictions.GroupBy(p => p.PredictionCategory);
        foreach (var group in grouped)
        {
            sb.AppendLine($"\n[{group.Key}]");
            foreach (var p in group)
            {
                // Compact: "EPL 15:00 Arsenal v Chelsea → Home Win | 2:1"
                var score = string.IsNullOrEmpty(p.ActualScore) ? "" : $" | {p.ActualScore}";
                sb.AppendLine($"{p.League} {p.Time} {p.HomeTeam} v {p.AwayTeam} → {p.PredictedOutcome}{score}");
            }
        }

        var summary = sb.ToString();

        lock (_cacheLock)
        {
            _cachedSummary = summary;
            _cachedDate = todayStr;
        }

        _logger.LogInformation("Built AI prediction context: {Length} chars, {Count} matches.", summary.Length, predictions.Count);
        return summary;
    }

    private static string BuildSystemPrompt(string predictionContext)
    {
        // return $"""
        //     You are Teddy, Lead Quantitative Football Analyst for MatchPredictor.
        //     You have today's algorithmic predictions (Poisson/xG models).

        //     {predictionContext}

        //     RULES:
        //     1. Only discuss matches from the data above. Never invent matches.
        //     2. Analytical tone — use terms like variance, xG, implied probability.
        //     3. "Best picks" = low variance, high-confidence predictions.
        //     4. Structure: 🟢 Bankers / 🟡 Value / 🔴 High Risk.
        //     5. Never guarantee wins. Acknowledge variance.
        //     6. Keep responses concise and scannable with markdown.
        //     """;

        return $"""
            You are Nelson, the Lead Quantitative Football Analyst for MatchPredictor. 
    
            You have exclusive access to today's algorithmic predictions generated by our Poisson distribution and Expected Goals (xG) variance models.
            Today's predictions data:
            {predictionContext}
            
            CONVERSATIONAL STYLE & TONE (CRITICAL):
            1. NO ROBOTIC GREETINGS: Never start your responses with "Hi, I am Teddy", "Welcome to MatchPredictor", or "As an AI...". Assume the user already knows who you are. Jump straight into the analysis.
            2. BE INTERACTIVE: Talk like a brilliant, analytical colleague. Instead of just dumping data and stopping, occasionally end your response with a highly relevant follow-up question to keep the conversation moving (e.g., "Are you looking to build a safe accumulator today, or hunting for high-value singles?", "Do you want me to break down the xG math on that specific match?").
            3. ACCESSIBLE QUANT TONE: Use data science terms ("variance," "expected goals," "implied probability," "market edge") naturally. Explain the 'why' behind the math, but keep it conversational, not like a textbook.
            
            CORE DIRECTIVES:
            4. GROUNDED REALITY: You may ONLY recommend or discuss matches explicitly listed in the data above. Do not invent matches, odds, or predictions.
            5. THE "BEST" PICKS: When asked for the best picks, prioritize low-variance setups (e.g., heavily favored Straight Wins in high-scoring games, or mathematically backed Over 2.5s).
            6. STRUCTURING ADVICE: For multiple picks, categorize cleanly: "🟢 High Confidence / Bankers", "🟡 Value / Moderate Variance", "🔴 High Risk / BTTS".
            7. RESPONSIBLE ANALYSIS: Never guarantee a win. Acknowledge the inherent variance in football (e.g., "Even a 75% probability loses 1 out of 4 times").
            
            FORMATTING RULES:
            - Keep responses concise, scannable, and highly structured.
            - For every match mentioned, explicitly state: [League] | [Home Team] vs [Away Team] | Prediction: [Outcome].
            - Use markdown (bolding, bullet points) to make the data easy to read.
            """;
    }

    /// <summary>
    /// Calls Gemini API using Polly policies configured in Program.cs.
    /// Uses gemini-2.0-flash-lite for higher free-tier rate limits.
    /// </summary>
    private async Task<string> CallGeminiWithRetryAsync(string apiKey, string prompt, CancellationToken ct)
    {
        var model = _configuration["GeminiModel"] ?? "gemini-1.5-flash";
        _logger.LogInformation("Calling Gemini model: {Model}", model);

        try
        {
            using var httpClient = _httpClientFactory.CreateClient("Gemini");

            var requestBody = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}",
                content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Gemini API error: {Status} {Body}", response.StatusCode,
                    errorBody[..Math.Min(300, errorBody.Length)]);
                
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    return "⏳ The AI service is currently busy (rate limit). Please wait a minute and try again.";
                }
                
                return $"❌ AI service error ({response.StatusCode}). Please try again later.";
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(responseJson);

            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return text ?? "No response generated.";
        }
        catch (TaskCanceledException)
        {
            return "⏳ Request timed out. Please try again.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Gemini API");
            return $"❌ Error communicating with AI: {ex.Message}";
        }
    }
}
