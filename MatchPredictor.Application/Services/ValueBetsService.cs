using System.Text.Json;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Application.Services;

public class ValueBetsService : IValueBetsService
{
    private const int MaxAiExplanationCount = 20;

    private readonly ApplicationDbContext _dbContext;
    private readonly IDataAnalyzerService _dataAnalyzerService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly IAiAdvisorService _aiAdvisorService;
    private readonly ISourceMarketPricingService _sourceMarketPricingService;
    private readonly PredictionSettings _settings;
    private readonly ILogger<ValueBetsService> _logger;

    public ValueBetsService(
        ApplicationDbContext dbContext,
        IDataAnalyzerService dataAnalyzerService,
        IThresholdTuningService thresholdTuningService,
        IAiAdvisorService aiAdvisorService,
        ISourceMarketPricingService sourceMarketPricingService,
        IOptions<PredictionSettings> options,
        ILogger<ValueBetsService> logger)
    {
        _dbContext = dbContext;
        _dataAnalyzerService = dataAnalyzerService;
        _thresholdTuningService = thresholdTuningService;
        _aiAdvisorService = aiAdvisorService;
        _sourceMarketPricingService = sourceMarketPricingService;
        _settings = options.Value;
        _logger = logger;
    }

    public async Task<IEnumerable<ValueBetDto>> GetTopValueBetsAsync(int limit = 60, CancellationToken ct = default)
    {
        var report = await GetValueBetReportAsync(limit, ct);
        return report.Bets;
    }

    public async Task<ValueBetReportDto> GetValueBetReportAsync(int limit = 60, CancellationToken ct = default)
    {
        var now = DateTimeProvider.GetLocalTime();
        var todayLocalDate = DateOnly.FromDateTime(now);
        var nowUtc = DateTime.UtcNow;
        var currentLocalTime = TimeOnly.FromDateTime(now);
        var exclusionCounts = CreateExclusionCountMap();
        var report = new ValueBetReportDto
        {
            GeneratedAtLocal = now
        };

        var upcomingMatches = await _dbContext.MatchDatas
            .AsNoTracking()
            .Where(m => m.MatchLocalDate == todayLocalDate)
            .Where(m =>
                (m.MatchDateTime.HasValue && m.MatchDateTime.Value >= nowUtc) ||
                (!m.MatchDateTime.HasValue && m.MatchLocalTime.HasValue && m.MatchLocalTime.Value >= currentLocalTime))
            .OrderBy(m => m.MatchDateTime)
            .ThenBy(m => m.MatchLocalTime)
            .ToListAsync(ct);

        if (!upcomingMatches.Any())
        {
            _logger.LogInformation("No upcoming matches available for Value Bets today.");
            report.ExclusionBreakdown = BuildExclusionBreakdown(exclusionCounts);
            return report;
        }

        IReadOnlyList<SourceMarketFixture> sourceMarketFixtures = [];
        try
        {
            sourceMarketFixtures = await _sourceMarketPricingService.GetTodaySourceMarketFixturesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load source market fixtures for Value Bets. Falling back to stored source probabilities only.");
        }

        var candidateBets = new List<ValueBetCandidate>();

        foreach (var match in upcomingMatches)
        {
            var forecastCandidates = _dataAnalyzerService.BuildForecastCandidates([match]);
            report.ConsideredCandidateCount += forecastCandidates.Count;
            var pricedCandidates = new List<ValueBetCandidate>();
            var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
                sourceMarketFixtures,
                match.HomeTeam,
                match.AwayTeam,
                match.League,
                ResolveScheduledUtc(match));

            foreach (var forecastCandidate in forecastCandidates)
            {
                if (!TryGetMarketProbability(
                        match,
                        sourceFixture,
                        forecastCandidate.Market,
                        out var marketProbability,
                        out var pricingSource,
                        out var oddsFreshness))
                {
                    IncrementExclusion(exclusionCounts, "no_source_price");
                    continue;
                }

                var thresholdDecision = ResolveThresholdDecision(forecastCandidate.Market);
                var calibratedProbability = Math.Clamp(forecastCandidate.CalibratedProbability, 0.0, 1.0);
                if (calibratedProbability < thresholdDecision.Threshold)
                {
                    IncrementExclusion(exclusionCounts, "below_threshold");
                    continue;
                }

                var edge = calibratedProbability - marketProbability;
                if (edge < _settings.ValueBetMinimumEdge)
                {
                    IncrementExclusion(exclusionCounts, "insufficient_edge");
                    continue;
                }

                pricedCandidates.Add(new ValueBetCandidate
                {
                    CandidateKey = BuildCandidateKey(
                        forecastCandidate.Date,
                        forecastCandidate.Time,
                        forecastCandidate.League,
                        forecastCandidate.HomeTeam,
                        forecastCandidate.AwayTeam,
                        forecastCandidate.PredictionCategory,
                        forecastCandidate.PredictedOutcome),
                    League = forecastCandidate.League,
                    HomeTeam = forecastCandidate.HomeTeam,
                    AwayTeam = forecastCandidate.AwayTeam,
                    KickoffTime = forecastCandidate.Time,
                    PredictionCategory = forecastCandidate.PredictionCategory,
                    PredictedOutcome = forecastCandidate.PredictedOutcome,
                    MathematicalProbability = calibratedProbability,
                    MarketProbability = marketProbability,
                    Edge = edge,
                    ThresholdUsed = thresholdDecision.Threshold,
                    ThresholdSource = thresholdDecision.ThresholdSource,
                    CalibratorUsed = forecastCandidate.CalibratorUsed,
                    PricingSource = pricingSource,
                    OddsFreshness = oddsFreshness,
                    EdgeSource = BuildEdgeSource(calibratedProbability, marketProbability)
                });
            }

            candidateBets.AddRange(SelectBestMatchCandidates(pricedCandidates));
        }

        var topCandidates = candidateBets
            .OrderByDescending(c => c.Edge)
            .ThenByDescending(c => c.MathematicalProbability)
            .ThenBy(c => c.KickoffTime)
            .Take(limit)
            .ToList();

        if (!topCandidates.Any())
        {
            report.ExclusionBreakdown = BuildExclusionBreakdown(exclusionCounts);
            return report;
        }

        foreach (var candidate in topCandidates)
        {
            candidate.AiJustification = BuildFallbackJustification(candidate);
        }

        var candidatesForAi = topCandidates.Take(MaxAiExplanationCount).ToList();
        if (candidatesForAi.Count > 0)
        {
            var payloadToAnalyze = JsonSerializer.Serialize(new
            {
                Picks = candidatesForAi.Select(candidate => new
                {
                    candidate.CandidateKey,
                    candidate.League,
                    candidate.HomeTeam,
                    candidate.AwayTeam,
                    candidate.KickoffTime,
                    candidate.PredictionCategory,
                    candidate.PredictedOutcome,
                    ModelProbabilityPct = Math.Round(candidate.MathematicalProbability * 100, 1),
                    MarketProbabilityPct = Math.Round(candidate.MarketProbability * 100, 1),
                    EdgePctPoints = Math.Round(candidate.Edge * 100, 1),
                    ThresholdPct = Math.Round(candidate.ThresholdUsed * 100, 1),
                    candidate.ThresholdSource,
                    candidate.CalibratorUsed
                })
            });

            try
            {
                var aiResponseJson = await _aiAdvisorService.AnalyzeValueBetsAsync(payloadToAnalyze, ct);

                if (aiResponseJson.StartsWith("❌") || aiResponseJson.StartsWith("⏳") || aiResponseJson.StartsWith("⚠️"))
                {
                    _logger.LogWarning("AI Advisor returned an error/warning for Value Bets: {Message}", aiResponseJson);
                }
                else
                {
                    var aiJustifications = ParseAiJustifications(aiResponseJson);
                    foreach (var candidate in topCandidates)
                    {
                        if (aiJustifications.TryGetValue(candidate.CandidateKey, out var justification) &&
                            !string.IsNullOrWhiteSpace(justification))
                        {
                            candidate.AiJustification = justification.Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falling back to deterministic Value Bet justifications after AI processing failed.");
            }
        }

        report.IncludedCandidateCount = topCandidates.Count;
        report.ExclusionBreakdown = BuildExclusionBreakdown(exclusionCounts);
        report.Bets = topCandidates.Select(candidate => candidate.ToDto()).ToList();
        return report;
    }

    private IEnumerable<ValueBetCandidate> SelectBestMatchCandidates(IEnumerable<ValueBetCandidate> candidates)
    {
        var candidateList = candidates.ToList();
        var selected = new List<ValueBetCandidate>();

        var bestOneX2Candidate = candidateList
            .Where(candidate => candidate.PredictionCategory is "StraightWin" or "Draw")
            .OrderByDescending(candidate => candidate.Edge)
            .ThenByDescending(candidate => candidate.MathematicalProbability)
            .FirstOrDefault();

        if (bestOneX2Candidate != null)
        {
            selected.Add(bestOneX2Candidate);
        }

        selected.AddRange(candidateList.Where(candidate => candidate.PredictionCategory is "Over2.5Goals" or "BothTeamsScore"));
        return selected;
    }

    private ThresholdDecision ResolveThresholdDecision(PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => _thresholdTuningService.GetThresholdDecision(PredictionMarket.BothTeamsScore, _settings.BttsScoreThreshold),
            PredictionMarket.Over25Goals => _thresholdTuningService.GetThresholdDecision(PredictionMarket.Over25Goals, _settings.OverTwoGoalsStrongThreshold),
            PredictionMarket.Draw => _thresholdTuningService.GetThresholdDecision(PredictionMarket.Draw, _settings.DrawStrongThreshold),
            PredictionMarket.HomeWin => _thresholdTuningService.GetThresholdDecision(PredictionMarket.HomeWin, _settings.HomeWinStrong),
            PredictionMarket.AwayWin => _thresholdTuningService.GetThresholdDecision(PredictionMarket.AwayWin, _settings.AwayWinStrong),
            _ => new ThresholdDecision
            {
                Threshold = 1.0,
                ThresholdSource = "Unsupported"
            }
        };
    }

    private static bool TryGetMarketProbability(
        MatchData match,
        SourceMarketFixture? sourceFixture,
        PredictionMarket market,
        out double marketProbability,
        out string pricingSource,
        out string oddsFreshness)
    {
        marketProbability = 0.0;
        pricingSource = string.Empty;
        oddsFreshness = string.Empty;

        if (TryGetLiveMarketProbability(sourceFixture, market, out marketProbability))
        {
            pricingSource = "Live source pull";
            oddsFreshness = "Fresh from today's source pricing pull.";
            return true;
        }

        if (market is PredictionMarket.HomeWin or PredictionMarket.Draw or PredictionMarket.AwayWin)
        {
            if (!match.TryGetNormalizedOneX2(out var oneX2))
            {
                return false;
            }

            marketProbability = market switch
            {
                PredictionMarket.HomeWin => oneX2.home,
                PredictionMarket.Draw => oneX2.draw,
                PredictionMarket.AwayWin => oneX2.away,
                _ => 0.0
            };
            pricingSource = "Stored sync snapshot";
            oddsFreshness = "Using the latest stored sync pricing for this fixture.";
            return true;
        }

        if (market == PredictionMarket.Over25Goals && match.TryGetNormalizedOver25Pair(out var overUnder25))
        {
            marketProbability = overUnder25.over25;
            pricingSource = "Stored sync snapshot";
            oddsFreshness = "Using the latest stored sync pricing for this fixture.";
            return true;
        }

        if (market == PredictionMarket.BothTeamsScore && match.TryGetNormalizedBttsPair(out var bttsPair))
        {
            marketProbability = bttsPair.yes;
            pricingSource = "Stored sync snapshot";
            oddsFreshness = "Using the latest stored sync pricing for this fixture.";
            return true;
        }

        return false;
    }

    private static bool TryGetLiveMarketProbability(SourceMarketFixture? sourceFixture, PredictionMarket market, out double marketProbability)
    {
        marketProbability = market switch
        {
            PredictionMarket.HomeWin when sourceFixture?.HomeWinProbability is double value => value,
            PredictionMarket.Draw when sourceFixture?.DrawProbability is double value => value,
            PredictionMarket.AwayWin when sourceFixture?.AwayWinProbability is double value => value,
            PredictionMarket.Over25Goals when sourceFixture?.Over25Probability is double value => value,
            PredictionMarket.BothTeamsScore when sourceFixture?.BttsYesProbability is double value => value,
            _ => 0.0
        };

        return marketProbability > 0;
    }

    private static DateTime? ResolveScheduledUtc(MatchData match)
    {
        if (match.MatchDateTime.HasValue)
        {
            return match.MatchDateTime.Value;
        }

        if (match.MatchLocalDate.HasValue)
        {
            var localTime = match.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(match.Time) ?? new TimeOnly(0, 0);
            return DateTimeProvider.ConvertLocalToUtc(match.MatchLocalDate.Value.ToDateTime(localTime, DateTimeKind.Unspecified));
        }

        return DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time).utcDateTime;
    }

    private static string BuildCandidateKey(
        string date,
        string kickoffTime,
        string league,
        string homeTeam,
        string awayTeam,
        string predictionCategory,
        string predictedOutcome)
    {
        return string.Join(
            "|",
            NormalizeKeyPart(date),
            NormalizeKeyPart(kickoffTime),
            NormalizeKeyPart(league),
            NormalizeKeyPart(homeTeam),
            NormalizeKeyPart(awayTeam),
            NormalizeKeyPart(predictionCategory),
            NormalizeKeyPart(predictedOutcome));
    }

    private static string NormalizeKeyPart(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static Dictionary<string, int> CreateExclusionCountMap()
    {
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["no_source_price"] = 0,
            ["below_threshold"] = 0,
            ["insufficient_edge"] = 0
        };
    }

    private static void IncrementExclusion(IDictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
    }

    private static List<ValueBetExclusionStat> BuildExclusionBreakdown(IReadOnlyDictionary<string, int> counts)
    {
        return
        [
            CreateExclusionStat(counts, "no_source_price", "No source price", "The match was skipped because no usable market probability was available for that market."),
            CreateExclusionStat(counts, "below_threshold", "Below threshold", "The calibrated model probability never cleared the market's publish threshold."),
            CreateExclusionStat(counts, "insufficient_edge", "Below edge floor", "The model leaned the right way, but not enough above the source market to count as value.")
        ];
    }

    private static ValueBetExclusionStat CreateExclusionStat(
        IReadOnlyDictionary<string, int> counts,
        string key,
        string label,
        string description)
    {
        counts.TryGetValue(key, out var count);
        return new ValueBetExclusionStat
        {
            Key = key,
            Label = label,
            Description = description,
            Count = count
        };
    }

    private static string BuildEdgeSource(double modelProbability, double marketProbability)
    {
        return $"Model {(modelProbability * 100):F1}% vs market {(marketProbability * 100):F1}%";
    }

    private static string BuildFallbackJustification(ValueBetCandidate candidate)
    {
        var modelPct = candidate.MathematicalProbability * 100;
        var marketPct = candidate.MarketProbability * 100;
        var edgePct = candidate.Edge * 100;
        var thresholdPct = candidate.ThresholdUsed * 100;
        var thresholdDescriptor = candidate.ThresholdSource.Equals("Tuned", StringComparison.OrdinalIgnoreCase)
            ? "tuned"
            : "configured";

        return $"{candidate.PredictedOutcome} is priced below our calibrated view: model {modelPct:F1}% vs market {marketPct:F1}% (+{edgePct:F1} pts). " +
               $"It clears the {thresholdDescriptor} {thresholdPct:F1}% threshold with the {candidate.CalibratorUsed} calibrator.";
    }

    private static Dictionary<string, string> ParseAiJustifications(string aiResponseJson)
    {
        using var document = JsonDocument.Parse(aiResponseJson);
        var picksElement = document.RootElement;

        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("picks", out var wrappedPicks))
        {
            picksElement = wrappedPicks;
        }

        if (picksElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("AI Value Bets response did not contain a picks array.");
        }

        var justifications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pickElement in picksElement.EnumerateArray())
        {
            if (!pickElement.TryGetProperty("CandidateKey", out var keyElement) ||
                !pickElement.TryGetProperty("AiJustification", out var justificationElement))
            {
                continue;
            }

            var candidateKey = keyElement.GetString();
            var justification = justificationElement.GetString();
            if (string.IsNullOrWhiteSpace(candidateKey) || string.IsNullOrWhiteSpace(justification))
            {
                continue;
            }

            justifications[candidateKey] = justification;
        }

        return justifications;
    }

    private sealed class ValueBetCandidate
    {
        public string CandidateKey { get; init; } = string.Empty;
        public string League { get; init; } = string.Empty;
        public string HomeTeam { get; init; } = string.Empty;
        public string AwayTeam { get; init; } = string.Empty;
        public string KickoffTime { get; init; } = string.Empty;
        public string PredictionCategory { get; init; } = string.Empty;
        public string PredictedOutcome { get; init; } = string.Empty;
        public double MathematicalProbability { get; init; }
        public double MarketProbability { get; init; }
        public double Edge { get; init; }
        public double ThresholdUsed { get; init; }
        public string ThresholdSource { get; init; } = "Configured";
        public string CalibratorUsed { get; init; } = "Bucket";
        public string PricingSource { get; init; } = "Stored sync snapshot";
        public string OddsFreshness { get; init; } = "Using the latest stored sync pricing for this fixture.";
        public string EdgeSource { get; init; } = string.Empty;
        public string AiJustification { get; set; } = string.Empty;

        public ValueBetDto ToDto()
        {
            return new ValueBetDto
            {
                League = League,
                HomeTeam = HomeTeam,
                AwayTeam = AwayTeam,
                KickoffTime = KickoffTime,
                PredictionCategory = PredictionCategory,
                PredictedOutcome = PredictedOutcome,
                MathematicalProbability = MathematicalProbability,
                MarketProbability = MarketProbability,
                Edge = Edge,
                ThresholdUsed = ThresholdUsed,
                ThresholdSource = ThresholdSource,
                CalibratorUsed = CalibratorUsed,
                PricingSource = PricingSource,
                OddsFreshness = OddsFreshness,
                EdgeSource = EdgeSource,
                AiJustification = AiJustification
            };
        }
    }
}
