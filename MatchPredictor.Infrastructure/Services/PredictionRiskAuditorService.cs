using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Statistics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public sealed class PredictionRiskAuditorService : IPredictionRiskAuditorService
{
    private const int MaxCandidatesPerRun = 12;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PredictionRiskAuditorService> _logger;

    public PredictionRiskAuditorService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PredictionRiskAuditorService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task AuditPublishedCandidatesAsync(
        IReadOnlyCollection<PredictionCandidate> publishedCandidates,
        CancellationToken cancellationToken = default)
    {
        if (publishedCandidates.Count == 0)
        {
            return;
        }

        var apiKey = _configuration["GroqApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("Skipping prediction risk audit because GroqApiKey is not configured.");
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            candidates = publishedCandidates
                .Take(MaxCandidatesPerRun)
                .Select(candidate =>
                {
                    var signalBreakdown = SignalBreakdownParser.TryParse(
                        candidate.FeatureContributionsJson,
                        candidate.PredictionCategory,
                        candidate.PredictedOutcome,
                        candidate.CalibratedProbability);

                    return new
                    {
                        fixture = $"{candidate.HomeTeam} vs {candidate.AwayTeam}",
                        candidate.League,
                        candidate.PredictionCategory,
                        candidate.PredictedOutcome,
                        calibratedConfidencePct = Math.Round(candidate.CalibratedProbability * 100, 1),
                        thresholdPct = Math.Round(candidate.ThresholdUsed * 100, 1),
                        candidate.ThresholdSource,
                        modelSignals = SignalBreakdownParser.ToPayloadObject(signalBreakdown),
                        signalAgreement = SignalBreakdownParser.ToAgreementPayloadObject(signalBreakdown)
                    };
                })
        });

        try
        {
            var response = await CallGroqAsync(apiKey, payload, cancellationToken);
            LogAuditResults(response, publishedCandidates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prediction risk audit failed; publish flow was not affected.");
        }
    }

    private void LogAuditResults(string rawResponse, int publishedCount)
    {
        if (!TryParseAuditResponse(rawResponse, out var audits))
        {
            _logger.LogInformation(
                "Prediction risk audit completed for {PublishedCount} published candidate(s) but returned non-JSON output.",
                publishedCount);
            return;
        }

        foreach (var audit in audits)
        {
            _logger.LogInformation(
                "Prediction risk audit: fixture={Fixture} market={Market} risk={RiskLevel} recommendPublish={RecommendPublish} flags={Flags} reason={Reason}",
                audit.Fixture,
                audit.Market,
                audit.RiskLevel,
                audit.RecommendPublish,
                string.Join(", ", audit.Flags),
                audit.Reason);
        }
    }

    private static bool TryParseAuditResponse(string rawResponse, out List<PredictionRiskAuditItem> audits)
    {
        audits = [];
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            if (document.RootElement.TryGetProperty("audits", out var auditsElement) &&
                auditsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in auditsElement.EnumerateArray())
                {
                    audits.Add(new PredictionRiskAuditItem
                    {
                        Fixture = item.TryGetProperty("fixture", out var fixture) ? fixture.GetString() ?? string.Empty : string.Empty,
                        Market = item.TryGetProperty("market", out var market) ? market.GetString() ?? string.Empty : string.Empty,
                        RiskLevel = item.TryGetProperty("riskLevel", out var risk) ? risk.GetString() ?? "low" : "low",
                        RecommendPublish = !item.TryGetProperty("recommendPublish", out var recommend) || recommend.GetBoolean(),
                        Reason = item.TryGetProperty("reason", out var reason) ? reason.GetString() ?? string.Empty : string.Empty,
                        Flags = item.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array
                            ? flags.EnumerateArray().Select(flag => flag.GetString() ?? string.Empty).Where(flag => flag.Length > 0).ToList()
                            : []
                    });
                }

                return audits.Count > 0;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private async Task<string> CallGroqAsync(string apiKey, string userPayload, CancellationToken cancellationToken)
    {
        var model = _configuration["GroqModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";
        var requestBody = new
        {
            model,
            temperature = 0.1,
            max_tokens = 1200,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = BuildRiskAuditorSystemPrompt() },
                new { role = "user", content = userPayload }
            }
        };

        using var client = _httpClientFactory.CreateClient("Groq");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Groq risk audit failed with status {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private static string BuildRiskAuditorSystemPrompt()
    {
        return """
            You are a football forecasting auditor. You receive candidates that already passed statistical thresholds.

            Your job: identify whether each pick has hidden risk NOT captured in the numeric signals.

            OUTPUT JSON:
            {
              "audits": [
                {
                  "fixture": "Home vs Away",
                  "market": "BothTeamsScore",
                  "riskLevel": "low|medium|high",
                  "flags": ["optional short flag"],
                  "recommendPublish": true,
                  "reason": "one sentence"
                }
              ]
            }

            RULES:
            - recommendPublish=false only when signalAgreement indicates thin history or strong disagreement AND calibrated confidence is only slightly above threshold.
            - Never change probabilities. Never invent injuries or lineups.
            - Default to recommendPublish=true when uncertain.
            - This is advisory logging only; be conservative with false recommendations.
            - Return one audit item per input candidate.
            """;
    }

    private sealed class PredictionRiskAuditItem
    {
        public string Fixture { get; init; } = string.Empty;
        public string Market { get; init; } = string.Empty;
        public string RiskLevel { get; init; } = "low";
        public bool RecommendPublish { get; init; } = true;
        public string Reason { get; init; } = string.Empty;
        public List<string> Flags { get; init; } = [];
    }
}
