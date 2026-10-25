using System.Globalization;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Services;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public sealed class QuickSlipService : IQuickSlipService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IChatCompletionsClient _chatClient;
    private readonly IAiSecurityGuardrailService _guardrails;
    private readonly ILogger<QuickSlipService> _logger;

    public QuickSlipService(
        ApplicationDbContext dbContext,
        IChatCompletionsClient chatClient,
        IAiSecurityGuardrailService guardrails,
        ILogger<QuickSlipService> logger)
    {
        _dbContext = dbContext;
        _chatClient = chatClient;
        _guardrails = guardrails;
        _logger = logger;
    }

    public async Task<QuickSlipParseResultDto> ParseAndStageAsync(QuickSlipParseRequest request, CancellationToken ct = default)
    {
        var rawInput = request.Input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new QuickSlipParseResultDto
            {
                Success = false,
                Summary = "Please enter what type of picks you want (e.g. '3 safe EPL home wins' or '4 GG matches tonight').",
                ClarificationQuestions = ["What market and how many games do you want to build into your slip?"]
            };
        }

        // Security Guardrail Pre-Check
        var guardrailResult = _guardrails.ValidateInboundPrompt(rawInput);
        if (!guardrailResult.IsAllowed)
        {
            _logger.LogWarning("QuickSlip input blocked by security guardrails: {Reason}", guardrailResult.Reason);
            return new QuickSlipParseResultDto
            {
                Success = false,
                Summary = guardrailResult.RefusalResponse?.Message ?? "Your input contains unsupported or restricted queries.",
                ClarificationQuestions = ["Please ask a question relating strictly to match predictions and betslips."]
            };
        }

        var refDate = request.ReferenceDate.HasValue 
            ? DateOnly.FromDateTime(request.ReferenceDate.Value) 
            : DateOnly.FromDateTime(DateTime.UtcNow);

        // Tier 1: Deterministic Parse
        var parsedIntent = QuickSlipParser.Parse(rawInput, refDate);
        var usedFallback = false;

        // If completely ambiguous, check if LLM can help disambiguate
        if (parsedIntent.IsAmbiguous && _chatClient.IsConfigured)
        {
            parsedIntent = await TryLlmDisambiguationAsync(rawInput, parsedIntent, ct);
            usedFallback = true;
        }

        if (parsedIntent.IsAmbiguous && parsedIntent.ClarificationQuestions.Count > 0)
        {
            return new QuickSlipParseResultDto
            {
                Success = false,
                Summary = "We need a bit more detail to assemble your slip.",
                ClarificationQuestions = parsedIntent.ClarificationQuestions.ToList(),
                UsedFallback = usedFallback
            };
        }

        // Tier 2: Candidate Matching from Database
        var stagedPicks = await MatchCandidatesAsync(parsedIntent, request.CurrentSlipPredictionIds, request.MaxLegs, ct);

        if (stagedPicks.Count == 0)
        {
            var noMatchMsg = BuildNoMatchExplanation(parsedIntent);
            return new QuickSlipParseResultDto
            {
                Success = false,
                Summary = noMatchMsg,
                ClarificationQuestions = ["Try broadening your odds criteria or choosing another league/market."],
                UsedFallback = usedFallback
            };
        }

        // Calculate Totals & EV
        decimal combinedOdds = 1.0m;
        decimal totalProb = 0m;
        decimal? totalEdge = 0m;
        var oddsCount = 0;

        foreach (var leg in stagedPicks)
        {
            if (leg.Odds.HasValue && leg.Odds.Value > 1.0m)
            {
                combinedOdds *= leg.Odds.Value;
                oddsCount++;
            }
            if (leg.ModelProbability.HasValue)
            {
                totalProb += leg.ModelProbability.Value;
            }
            if (leg.ExpectedValue.HasValue)
            {
                totalEdge += leg.ExpectedValue.Value;
            }
        }

        var avgProb = stagedPicks.Count > 0 ? totalProb / stagedPicks.Count : 0m;
        var avgEdge = stagedPicks.Count > 0 && totalEdge.HasValue ? totalEdge.Value / stagedPicks.Count : (decimal?)null;

        var summary = BuildSuccessSummary(stagedPicks.Count, combinedOdds, parsedIntent);

        return new QuickSlipParseResultDto
        {
            Success = true,
            Summary = summary,
            StagedPicks = stagedPicks,
            TotalCombinedOdds = Math.Round(combinedOdds, 2),
            AverageModelProbability = Math.Round(avgProb, 4),
            OverallExpectedEdge = avgEdge.HasValue ? Math.Round(avgEdge.Value, 4) : null,
            UsedFallback = usedFallback,
            ActionDirective = "Stage"
        };
    }

    private async Task<List<StagedSlipLegDto>> MatchCandidatesAsync(
        QuickSlipParsedIntent intent,
        List<int>? excludePredictionIds,
        int? requestedMaxLegs,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var targetDate = intent.TargetDate ?? DateOnly.FromDateTime(nowUtc);
        var endDate = targetDate.AddDays(2); // Look up to 2 days ahead for matching fixtures

        // Query active predictions
        var query = _dbContext.Predictions
            .AsNoTracking()
            .Where(p => p.IsCurrentRevision && p.WasPublished)
            .Where(p => p.MatchLocalDate >= targetDate && p.MatchLocalDate <= endDate);

        if (excludePredictionIds is { Count: > 0 })
        {
            query = query.Where(p => !excludePredictionIds.Contains(p.Id));
        }

        // Filter by League if specified
        if (!string.IsNullOrWhiteSpace(intent.LeagueHint))
        {
            var leagueLower = intent.LeagueHint.ToLower();
            query = query.Where(p => p.League.ToLower().Contains(leagueLower));
        }

        // Filter by Teams if specified
        if (intent.TeamHints.Count > 0)
        {
            query = query.Where(p => intent.TeamHints.Any(t => p.HomeTeam.Contains(t) || p.AwayTeam.Contains(t)));
        }

        // Filter by Markets if specified
        if (intent.RequestedMarkets.Count > 0)
        {
            var categories = intent.RequestedMarkets.Select(m => m.ToCategory()).ToList();
            query = query.Where(p => categories.Contains(p.PredictionCategory));
        }

        var predictions = await query.ToListAsync(ct);

        // Filter out matches that already kicked off
        predictions = predictions
            .Where(p => !p.MatchDateTime.HasValue || p.MatchDateTime.Value > nowUtc.AddMinutes(-5))
            .ToList();

        if (predictions.Count == 0)
        {
            return [];
        }

        // Load odds from MatchDatas
        var dates = predictions.Select(p => p.MatchLocalDate).Distinct().ToList();
        var matchDatas = await _dbContext.MatchDatas
            .AsNoTracking()
            .Where(m => m.MatchLocalDate.HasValue && dates.Contains(m.MatchLocalDate.Value))
            .ToListAsync(ct);

        var matchDataLookup = matchDatas
            .GroupBy(m => $"{m.MatchLocalDate:yyyy-MM-dd}_{m.HomeTeam}_{m.AwayTeam}".ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var legs = new List<StagedSlipLegDto>();

        foreach (var pred in predictions)
        {
            var key = $"{pred.MatchLocalDate:yyyy-MM-dd}_{pred.HomeTeam}_{pred.AwayTeam}".ToLowerInvariant();
            matchDataLookup.TryGetValue(key, out var md);

            var odds = ResolveOddsForPrediction(pred, md);
            var prob = pred.ConfidenceScore;
            decimal? ev = (odds.HasValue && prob.HasValue && odds.Value > 1.0m)
                ? (prob.Value * odds.Value) - 1.0m
                : null;

            // Apply Odds Filter
            if (intent.MinOdds.HasValue && odds.HasValue && odds.Value < intent.MinOdds.Value) continue;
            if (intent.MaxOdds.HasValue && odds.HasValue && odds.Value > intent.MaxOdds.Value) continue;

            legs.Add(new StagedSlipLegDto
            {
                PredictionId = pred.Id,
                HomeTeam = pred.HomeTeam,
                AwayTeam = pred.AwayTeam,
                League = pred.League,
                Market = pred.PredictionCategory,
                Prediction = pred.PredictedOutcome,
                Odds = odds,
                ModelProbability = prob,
                ExpectedValue = ev,
                MatchDateTimeUtc = pred.MatchDateTime,
                FormattedTime = pred.MatchLocalTime.HasValue 
                    ? pred.MatchLocalTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture) 
                    : pred.Time
            });
        }

        // Rank by Bias
        IOrderedEnumerable<StagedSlipLegDto> ranked = intent.Bias switch
        {
            "Safe" => legs.OrderByDescending(l => l.ModelProbability ?? 0m).ThenBy(l => l.Odds ?? 99m),
            "Value" => legs.OrderByDescending(l => l.ExpectedValue ?? -1m).ThenByDescending(l => l.Odds ?? 0m),
            _ => legs.OrderByDescending(l => (l.ModelProbability ?? 0m) * (l.Odds ?? 1m))
        };

        var targetCount = requestedMaxLegs ?? intent.RequestedCount ?? 3;
        targetCount = Math.Clamp(targetCount, 1, 10);

        return ranked.Take(targetCount).ToList();
    }

    private static decimal? ResolveOddsForPrediction(Prediction prediction, MatchData? matchData)
    {
        if (matchData is null) return null;

        var cat = prediction.PredictionCategory;
        var outcome = prediction.PredictedOutcome ?? string.Empty;

        double rawOdds = cat switch
        {
            "BothTeamsScore" => matchData.BttsYes > 1.0 ? matchData.BttsYes : 0,
            "Over2.5Goals" => matchData.OverTwoGoals > 1.0 ? matchData.OverTwoGoals : 0,
            "Under2.5Goals" => matchData.UnderTwoGoals > 1.0 ? matchData.UnderTwoGoals : 0,
            "StraightWin" => outcome.Contains("Home", StringComparison.OrdinalIgnoreCase) || outcome.Contains(prediction.HomeTeam, StringComparison.OrdinalIgnoreCase)
                ? matchData.HomeWin
                : matchData.AwayWin,
            "Draw" => matchData.Draw > 1.0 ? matchData.Draw : 0,
            _ => 0
        };

        return rawOdds > 1.0 ? (decimal)Math.Round(rawOdds, 2) : null;
    }

    private static string BuildSuccessSummary(int legCount, decimal combinedOdds, QuickSlipParsedIntent intent)
    {
        var marketLabel = intent.RequestedMarkets.Count switch
        {
            1 => intent.RequestedMarkets[0].ToDisplayName(),
            > 1 => "mixed market",
            _ => "featured"
        };

        var leagueLabel = !string.IsNullOrWhiteSpace(intent.LeagueHint) ? $" in {intent.LeagueHint}" : "";
        var biasLabel = intent.Bias == "Safe" ? " high-confidence" : intent.Bias == "Value" ? " positive-EV value" : "";

        return $"Staged {legCount}{biasLabel} {marketLabel} picks{leagueLabel} with {combinedOdds:F2} total combined odds.";
    }

    private static string BuildNoMatchExplanation(QuickSlipParsedIntent intent)
    {
        var criteria = new List<string>();
        if (intent.RequestedMarkets.Count > 0)
        {
            criteria.Add($"market ({string.Join(", ", intent.RequestedMarkets.Select(m => m.ToDisplayName()))})");
        }
        if (!string.IsNullOrWhiteSpace(intent.LeagueHint))
        {
            criteria.Add($"league '{intent.LeagueHint}'");
        }
        if (intent.MinOdds.HasValue || intent.MaxOdds.HasValue)
        {
            criteria.Add($"odds range [{intent.MinOdds?.ToString("F2") ?? "any"} - {intent.MaxOdds?.ToString("F2") ?? "any"}]");
        }

        var criteriaStr = criteria.Count > 0 ? string.Join(", ", criteria) : "your specific filters";
        return $"No upcoming unstarted matches currently match {criteriaStr}.";
    }

    private async Task<QuickSlipParsedIntent> TryLlmDisambiguationAsync(
        string userPrompt,
        QuickSlipParsedIntent initial,
        CancellationToken ct)
    {
        try
        {
            var system = """
                You are MatchPredictor Slip Assistant. Convert a betting prompt into structured JSON.
                Supported Markets: BothTeamsScore, Over25Goals, Under25Goals, StraightWin, Draw.
                Output Schema:
                {
                  "markets": ["BothTeamsScore" | "Over25Goals" | "Under25Goals" | "StraightWin" | "Draw"],
                  "count": number or null,
                  "bias": "Safe" | "Value" | "Balanced",
                  "league": string or null,
                  "teams": [string],
                  "minOdds": number or null,
                  "maxOdds": number or null
                }
                """;

            var result = await _chatClient.CompleteAsync(new ChatCompletionsRequest
            {
                Temperature = 0.0,
                JsonMode = true,
                MaxTokens = 300,
                Messages =
                [
                    new ChatCompletionsMessage { Role = "system", Content = system },
                    new ChatCompletionsMessage { Role = "user", Content = userPrompt }
                ]
            }, ct);

            if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(result.Content);
                var root = doc.RootElement;

                var markets = new List<PredictionMarket>();
                if (root.TryGetProperty("markets", out var mArray) && mArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var elem in mArray.EnumerateArray())
                    {
                        var mStr = elem.GetString();
                        if (Enum.TryParse<PredictionMarket>(mStr, true, out var pm))
                        {
                            markets.Add(pm);
                        }
                    }
                }

                int? count = root.TryGetProperty("count", out var cProp) && cProp.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? cProp.GetInt32()
                    : initial.RequestedCount;

                var bias = root.TryGetProperty("bias", out var bProp) ? bProp.GetString() ?? initial.Bias : initial.Bias;
                var league = root.TryGetProperty("league", out var lProp) ? lProp.GetString() ?? initial.LeagueHint : initial.LeagueHint;

                var teams = new List<string>(initial.TeamHints);
                if (root.TryGetProperty("teams", out var tArray) && tArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var elem in tArray.EnumerateArray())
                    {
                        if (elem.GetString() is { Length: > 0 } t && !teams.Contains(t)) teams.Add(t);
                    }
                }

                decimal? minOdds = root.TryGetProperty("minOdds", out var minProp) && minProp.ValueKind == System.Text.Json.JsonValueKind.Number ? minProp.GetDecimal() : initial.MinOdds;
                decimal? maxOdds = root.TryGetProperty("maxOdds", out var maxProp) && maxProp.ValueKind == System.Text.Json.JsonValueKind.Number ? maxProp.GetDecimal() : initial.MaxOdds;

                var isStillAmbiguous = markets.Count == 0 && teams.Count == 0;
                var questions = isStillAmbiguous 
                    ? new List<string> { "Which betting market or teams would you like to target?" }
                    : new List<string>();

                return new QuickSlipParsedIntent(
                    markets.Count > 0 ? markets : initial.RequestedMarkets,
                    count,
                    minOdds,
                    maxOdds,
                    initial.TargetCombinedOdds,
                    bias,
                    league,
                    teams,
                    initial.TargetDate,
                    isStillAmbiguous,
                    questions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Quick slip LLM disambiguation failed. Keeping deterministic intent.");
        }

        return initial;
    }
}
