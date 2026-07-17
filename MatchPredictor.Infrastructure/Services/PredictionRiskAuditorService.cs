using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services.Llm;
using MatchPredictor.Infrastructure.Statistics;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public sealed class PredictionRiskAuditorService : IPredictionRiskAuditorService
{
    private const int MaxCandidatesPerRun = 12;

    private readonly IChatCompletionsClient _chatClient;
    private readonly ILogger<PredictionRiskAuditorService> _logger;

    public PredictionRiskAuditorService(
        IChatCompletionsClient chatClient,
        ILogger<PredictionRiskAuditorService> logger)
    {
        _chatClient = chatClient;
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

        if (!_chatClient.IsConfigured)
        {
            _logger.LogDebug("Skipping prediction risk audit because AI API key is not configured.");
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
            var response = await CallRiskAuditAsync(payload, cancellationToken);
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

    private async Task<string> CallRiskAuditAsync(string userPayload, CancellationToken cancellationToken)
    {
        var result = await _chatClient.CompleteAsync(
            new ChatCompletionsRequest
            {
                Temperature = 0.1,
                MaxTokens = 1200,
                JsonMode = true,
                Messages =
                [
                    new ChatCompletionsMessage { Role = "system", Content = BuildRiskAuditorSystemPrompt() },
                    new ChatCompletionsMessage { Role = "user", Content = userPayload }
                ]
            },
            cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"AI risk audit failed with status {result.StatusCode}: {result.ErrorBody ?? result.ExceptionMessage}");
        }

        return result.Content;
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
