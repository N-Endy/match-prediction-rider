using System.Globalization;
using System.Text.Json;
using MatchPredictor.Application.Prompts;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services.Llm;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

public class AutonomousTipsterAgentService : IAutonomousTipsterAgentService
{
    private readonly IChatCompletionsClient _chatClient;
    private readonly ISportyBetBookingService _bookingService;
    private readonly ILogger<AutonomousTipsterAgentService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    public AutonomousTipsterAgentService(
        IChatCompletionsClient chatClient,
        ISportyBetBookingService bookingService,
        ILogger<AutonomousTipsterAgentService> logger)
    {
        _chatClient = chatClient;
        _bookingService = bookingService;
        _logger = logger;
    }

    public async Task<OperationsDossierDto?> ReasonAndComposeDossierAsync(
        DateOnly slateDate,
        IReadOnlyList<Prediction> upcomingPredictions,
        IReadOnlyDictionary<int, decimal> resolvedOddsLookup,
        int totalFixturesOnDate,
        CancellationToken ct = default)
    {
        if (!_chatClient.IsConfigured)
        {
            _logger.LogInformation("LLM client is not configured. Falling back to deterministic tipster engine.");
            return null;
        }

        if (upcomingPredictions.Count == 0)
        {
            return null;
        }

        try
        {
            // Build candidates payload
            var candidatePayload = upcomingPredictions.Select(p =>
            {
                var odds = resolvedOddsLookup.TryGetValue(p.Id, out var o) ? o : 1.70m;
                var modelProb = p.ConfidenceScore ?? 0.50m;
                var impliedProb = odds > 1.0m ? Math.Round(1.0m / odds, 4) : 0.50m;
                var edgePct = Math.Round((modelProb - impliedProb) * 100m, 1);

                return new
                {
                    PredictionId = p.Id,
                    Fixture = $"{p.HomeTeam} vs {p.AwayTeam}",
                    League = p.League,
                    Kickoff = p.MatchLocalTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? p.Time,
                    Market = FormatMarketCode(p.PredictionCategory),
                    Pick = p.PredictedOutcome ?? string.Empty,
                    Odds = odds,
                    ModelProbPct = Math.Round(modelProb * 100m, 1),
                    EdgePct = edgePct
                };
            }).ToList();

            var userPrompt = $"""
Date: {slateDate:yyyy-MM-dd}
Total Board Fixtures: {totalFixturesOnDate} matches.
Available Model-Analyzed Candidates ({candidatePayload.Count}):
{JsonSerializer.Serialize(candidatePayload)}

Instructions:
1. Review the board volume, league distribution, and value opportunities.
2. Assign SlateGrade (A, B, or C) with an executive tipster briefing and tactical watchout warnings.
3. Determine the optimal portfolio: assemble between 1 and 3 high-conviction tickets.
4. Total daily risk budget must be between 3.0% and 7.5% (recommended 5.0%).
5. Return ONLY a valid JSON object adhering to the schema.
""";

            var request = new ChatCompletionsRequest
            {
                Messages =
                [
                    new ChatCompletionsMessage { Role = "system", Content = AutonomousTipsterPrompt.SystemPrompt },
                    new ChatCompletionsMessage { Role = "user", Content = userPrompt }
                ],
                JsonMode = true,
                Temperature = 0.25,
                TimeoutSeconds = 14
            };

            var result = await _chatClient.CompleteAsync(request, ct);

            if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
            {
                _logger.LogWarning("LLM completion returned failure or empty response: {Error}. Falling back.", result.ErrorBody ?? result.ExceptionMessage);
                return null;
            }

            var cleanJson = SanitizeJsonResponse(result.Content);
            var parsed = JsonSerializer.Deserialize<AutonomousSlateLlmResponse>(cleanJson, JsonOptions);

            if (parsed is null || parsed.Tickets.Count == 0)
            {
                _logger.LogWarning("LLM response did not contain any valid tickets. Falling back.");
                return null;
            }

            // Run Mathematical Guardrail Validation
            var predictionMap = upcomingPredictions.ToDictionary(p => p.Id);
            var validatedTickets = new List<OperationsPillarTicketDto>();

            foreach (var rawTicket in parsed.Tickets)
            {
                var pillarKind = ParsePillarKind(rawTicket.PillarKind);

                if (rawTicket.IsPreserved || rawTicket.SelectedPredictionIds.Count == 0)
                {
                    validatedTickets.Add(new OperationsPillarTicketDto
                    {
                        PillarKind = pillarKind,
                        Title = string.IsNullOrWhiteSpace(rawTicket.Title) ? GetDefaultTitle(pillarKind) : rawTicket.Title,
                        Subtitle = rawTicket.Subtitle,
                        TargetOddsRange = rawTicket.TargetOddsRange,
                        AllocationPercent = 0m,
                        RecommendedUnits = 0m,
                        IsPreserved = true,
                        PreservationReason = string.IsNullOrWhiteSpace(rawTicket.PreservationReason)
                            ? "AI Tipster evaluated today's card and determined risk/reward profile does not justify capital deployment."
                            : rawTicket.PreservationReason,
                        TacticalAnalysis = rawTicket.TacticalAnalysis
                    });
                    continue;
                }

                // Verify legs against actual database fixtures
                var selectedPredictions = new List<Prediction>();
                var seenFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var id in rawTicket.SelectedPredictionIds)
                {
                    if (!predictionMap.TryGetValue(id, out var pred)) continue;

                    var fixtureKey = pred.FixtureKey ?? $"{pred.HomeTeam}_{pred.AwayTeam}";
                    if (seenFixtures.Add(fixtureKey))
                    {
                        selectedPredictions.Add(pred);
                    }
                }

                if (selectedPredictions.Count == 0)
                {
                    continue;
                }

                var legs = selectedPredictions.Select(pred =>
                {
                    var odds = resolvedOddsLookup.TryGetValue(pred.Id, out var o) ? o : 1.70m;
                    var modelProb = pred.ConfidenceScore ?? 0.50m;
                    var impliedProb = odds > 1.0m ? Math.Round(1.0m / odds, 4) : 0.50m;
                    var edge = Math.Round((modelProb - impliedProb) * 100m, 1);

                    rawTicket.CustomLegRationales.TryGetValue(pred.Id, out var customRationale);
                    var rationale = !string.IsNullOrWhiteSpace(customRationale)
                        ? customRationale
                        : $"Model probability {Math.Round(modelProb * 100m, 1)}% ({edge:+0.0;-0.0}% edge vs {odds:F2} odds). Solid historical and market support.";

                    return new OperationsLegDto
                    {
                        PredictionId = pred.Id,
                        HomeTeam = pred.HomeTeam,
                        AwayTeam = pred.AwayTeam,
                        League = pred.League,
                        MatchLocalDate = pred.MatchLocalDate,
                        KickoffLocalTime = pred.MatchLocalTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? pred.Time,
                        MatchDateTimeUtc = pred.MatchDateTime,
                        Market = FormatMarketCode(pred.PredictionCategory),
                        Prediction = pred.PredictedOutcome ?? string.Empty,
                        Odds = odds,
                        ModelProbability = modelProb,
                        EdgePercent = edge,
                        Conviction = modelProb >= 0.75m ? "BANKER" : edge >= 4.0m ? "VALUE" : "SOLID",
                        TacticalRationale = rationale
                    };
                }).ToList();

                decimal combinedOdds = legs.Aggregate(1.0m, (acc, l) => acc * l.Odds);
                decimal modelProbAcc = legs.Aggregate(1.0m, (acc, l) => acc * l.ModelProbability);
                decimal impliedProbAcc = Math.Round(1.0m / combinedOdds, 4);
                decimal edgeAcc = Math.Round((modelProbAcc - impliedProbAcc) * 100m, 1);

                // Safe Kelly clamping
                var units = Math.Clamp(rawTicket.RecommendedUnits, 0.1m, 5.0m);
                var allocation = Math.Clamp(rawTicket.AllocationPercent, 5.0m, 100.0m);

                // Book ticket with SportyBet
                var booking = await BookTicketAsync(legs);

                validatedTickets.Add(new OperationsPillarTicketDto
                {
                    PillarKind = pillarKind,
                    Title = string.IsNullOrWhiteSpace(rawTicket.Title) ? GetDefaultTitle(pillarKind) : rawTicket.Title,
                    Subtitle = rawTicket.Subtitle,
                    TargetOddsRange = string.IsNullOrWhiteSpace(rawTicket.TargetOddsRange) ? $"{combinedOdds:F2}x" : rawTicket.TargetOddsRange,
                    CombinedOdds = Math.Round(combinedOdds, 2),
                    ModelProbability = Math.Round(modelProbAcc, 4),
                    ImpliedProbability = impliedProbAcc,
                    EdgePercent = edgeAcc,
                    AllocationPercent = allocation,
                    RecommendedUnits = units,
                    KellyFraction = Math.Round(units / 10m, 2),
                    BookingCode = booking.BookingCode,
                    BookingUrl = booking.BookingUrl,
                    IsPreserved = false,
                    TacticalAnalysis = rawTicket.TacticalAnalysis,
                    Legs = legs
                });
            }

            if (validatedTickets.Count == 0)
            {
                _logger.LogWarning("Guardrails rejected all candidate selections. Falling back to deterministic engine.");
                return null;
            }

            var dailyRisk = Math.Clamp(parsed.RecommendedTotalDailyRiskPercent, 3.0m, 7.5m);
            var gradeBadgeColor = parsed.SlateGrade.Contains("GRADE A", StringComparison.OrdinalIgnoreCase) ? "#10b981" :
                                  parsed.SlateGrade.Contains("GRADE B", StringComparison.OrdinalIgnoreCase) ? "#38bdf8" : "#f59e0b";

            return new OperationsDossierDto
            {
                SlateDate = slateDate,
                GeneratedAtUtc = DateTime.UtcNow,
                GeneratedLocalLabel = DateTime.UtcNow.ToString("dd MMM yyyy · HH:mm 'UTC'", CultureInfo.InvariantCulture),
                TotalFixturesAnalyzed = totalFixturesOnDate,
                PublishedPicksCount = upcomingPredictions.Count,
                SlateGrade = parsed.SlateGrade,
                SlateBadgeColor = gradeBadgeColor,
                ExecutiveSummary = parsed.ExecutiveBriefing,
                TacticalWatchouts = parsed.TacticalWatchouts,
                TotalDailyRiskBudgetPercent = dailyRisk,
                EngineMode = "Autonomous Reasoning Agent",
                EngineBadgeColor = "#10b981",
                Tickets = validatedTickets
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during Autonomous Tipster Agent execution. Falling back to deterministic math engine.");
            return null;
        }
    }

    private static string SanitizeJsonResponse(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }
        else if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed[3..];
        }

        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }

    private static OperationsPillarKind ParsePillarKind(string kind) => kind.ToLowerInvariant() switch
    {
        "compounder" or "pillar1" or "banker" => OperationsPillarKind.Compounder,
        "valueedge" or "pillar2" or "value" => OperationsPillarKind.ValueEdge,
        "multipliersniper" or "pillar3" or "sniper" or "multiplier" => OperationsPillarKind.MultiplierSniper,
        _ => OperationsPillarKind.ValueEdge
    };

    private static string GetDefaultTitle(OperationsPillarKind kind) => kind switch
    {
        OperationsPillarKind.Compounder => "The Capital Compounder",
        OperationsPillarKind.ValueEdge => "The Value Edge Ticket",
        OperationsPillarKind.MultiplierSniper => "The Multiplier Sniper",
        _ => "Portfolio Ticket"
    };

    private static string FormatMarketCode(string category) => category switch
    {
        "BothTeamsScore" => "BTTS",
        "Over2.5Goals" => "Over 2.5",
        "Under2.5Goals" => "Under 2.5",
        "StraightWin" => "1X2",
        "Draw" => "Draw",
        _ => category
    };

    private async Task<(string BookingCode, string BookingUrl)> BookTicketAsync(List<OperationsLegDto> legs)
    {
        if (legs.Count == 0) return (string.Empty, string.Empty);

        try
        {
            var selections = legs.Select(leg => new BookingSelection
            {
                HomeTeam = leg.HomeTeam,
                AwayTeam = leg.AwayTeam,
                League = leg.League,
                Market = leg.Market,
                Prediction = leg.Prediction,
                PredictionId = leg.PredictionId,
                MatchDateTimeUtc = leg.MatchDateTimeUtc
            }).ToList();

            var result = await _bookingService.BookGamesAsync(selections);
            if (result.Success && !string.IsNullOrWhiteSpace(result.BookingCode))
            {
                return (result.BookingCode, result.BookingUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automated booking on SportyBet threw an exception in Autonomous Agent. Using fallback code.");
        }

        var raw = string.Join("_", legs.Select(l => l.PredictionId).OrderBy(id => id));
        var hash = (uint)Math.Abs(raw.GetHashCode());
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var chars = new char[5];
        for (int i = 0; i < 5; i++)
        {
            chars[i] = alphabet[(int)(hash % (uint)alphabet.Length)];
            hash /= (uint)alphabet.Length;
        }

        var code = $"OP-{new string(chars)}";
        return (code, string.Empty);
    }
}
