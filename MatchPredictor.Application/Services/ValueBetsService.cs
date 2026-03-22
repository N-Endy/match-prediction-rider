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
    private readonly ApplicationDbContext _dbContext;
    private readonly IDataAnalyzerService _dataAnalyzerService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly ISourceMarketPricingService _sourceMarketPricingService;
    private readonly PredictionSettings _settings;
    private readonly ILogger<ValueBetsService> _logger;

    public ValueBetsService(
        ApplicationDbContext dbContext,
        IDataAnalyzerService dataAnalyzerService,
        IThresholdTuningService thresholdTuningService,
        ISourceMarketPricingService sourceMarketPricingService,
        IOptions<PredictionSettings> options,
        ILogger<ValueBetsService> logger)
    {
        _dbContext = dbContext;
        _dataAnalyzerService = dataAnalyzerService;
        _thresholdTuningService = thresholdTuningService;
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

        if (upcomingMatches.Count == 0)
        {
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
            _logger.LogWarning(ex, "Failed to load tennis source pricing for value bets.");
            AddWarning(report, "Live tennis winner pricing is unavailable right now, so value bets may be empty.");
        }

        var currentPredictionLookup = await LoadCurrentPredictionLookupAsync(todayLocalDate, ct);
        var candidateBets = new List<ValueBetCandidate>();

        foreach (var match in upcomingMatches)
        {
            var matchFixtureKey = FixtureIdentityFactory.FromMatchData(match).FixtureKey;
            var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
                sourceMarketFixtures,
                match.HomeTeam,
                match.AwayTeam,
                match.League,
                match.MatchDateTime);

            foreach (var forecastCandidate in _dataAnalyzerService.BuildForecastCandidates([match]))
            {
                report.ConsideredCandidateCount++;

                if (!IsSupportedMarket(forecastCandidate.Market))
                {
                    IncrementExclusion(exclusionCounts, "unsupported_market");
                    continue;
                }

                if (sourceFixture is null)
                {
                    IncrementExclusion(exclusionCounts, "no_source_fixture");
                    continue;
                }

                if (!MarketQuoteResolver.TryResolve(match, sourceFixture, forecastCandidate.Market, out var marketQuote))
                {
                    IncrementExclusion(
                        exclusionCounts,
                        HasExactSourceSelection(sourceFixture, forecastCandidate.PredictionCategory, forecastCandidate.PredictedOutcome)
                            ? "no_source_price"
                            : "no_exact_line");
                    continue;
                }

                var thresholdDecision = ResolveThresholdDecision(forecastCandidate.Market);
                var calibratedProbability = Math.Clamp(forecastCandidate.CalibratedProbability, 0.0, 1.0);
                if (calibratedProbability < thresholdDecision.Threshold)
                {
                    IncrementExclusion(exclusionCounts, "below_threshold");
                    continue;
                }

                var edge = calibratedProbability - marketQuote.MarketProbability;
                if (edge < _settings.ValueBetMinimumEdge)
                {
                    IncrementExclusion(exclusionCounts, "insufficient_edge");
                    continue;
                }

                currentPredictionLookup.TryGetValue(
                    BuildCurrentPredictionLookupKey(matchFixtureKey, forecastCandidate.PredictionCategory, forecastCandidate.PredictedOutcome),
                    out var linkedPrediction);

                candidateBets.Add(new ValueBetCandidate
                {
                    CandidateKey = BuildCandidateKey(
                        forecastCandidate.Date,
                        forecastCandidate.Time,
                        forecastCandidate.League,
                        forecastCandidate.HomeTeam,
                        forecastCandidate.AwayTeam,
                        forecastCandidate.PredictionCategory,
                        forecastCandidate.PredictedOutcome),
                    FixtureKey = matchFixtureKey,
                    PredictionId = linkedPrediction?.Id,
                    MatchDateTimeUtc = match.MatchDateTime,
                    League = forecastCandidate.League,
                    HomeTeam = forecastCandidate.HomeTeam,
                    AwayTeam = forecastCandidate.AwayTeam,
                    KickoffTime = forecastCandidate.Time,
                    PredictionCategory = forecastCandidate.PredictionCategory,
                    PredictedOutcome = forecastCandidate.PredictedOutcome,
                    MathematicalProbability = calibratedProbability,
                    MarketProbability = marketQuote.MarketProbability,
                    DecimalOdds = marketQuote.DecimalOdds,
                    ImpliedProbability = marketQuote.ImpliedProbability,
                    ExpectedValuePercent = BetPricingMath.CalculateExpectedValuePercent(calibratedProbability, marketQuote.DecimalOdds) ?? 0d,
                    Edge = edge,
                    ThresholdUsed = thresholdDecision.Threshold,
                    ThresholdSource = thresholdDecision.ThresholdSource,
                    CalibratorUsed = forecastCandidate.CalibratorUsed,
                    PricingSource = marketQuote.PricingSource,
                    OddsFreshness = marketQuote.OddsFreshness,
                    OddsDerivationSource = marketQuote.OddsDerivationSource,
                    EdgeSource = BuildEdgeSource(calibratedProbability, marketQuote.MarketProbability),
                    AiJustification = BuildFallbackJustification(forecastCandidate.PredictedOutcome, calibratedProbability, marketQuote.DecimalOdds, edge, thresholdDecision)
                });
            }
        }

        var topCandidates = candidateBets
            .GroupBy(candidate => BuildMarketKey(candidate.FixtureKey, candidate.PredictionCategory, candidate.PredictedOutcome))
            .Select(group => group
                .OrderByDescending(candidate => candidate.ExpectedValuePercent)
                .ThenByDescending(candidate => candidate.Edge)
                .ThenByDescending(candidate => candidate.MathematicalProbability)
                .First())
            .OrderByDescending(candidate => candidate.ExpectedValuePercent)
            .ThenByDescending(candidate => candidate.Edge)
            .ThenByDescending(candidate => candidate.MathematicalProbability)
            .Take(limit)
            .ToList();

        report.IncludedCandidateCount = topCandidates.Count;
        report.ExclusionBreakdown = BuildExclusionBreakdown(exclusionCounts);
        report.Bets = topCandidates.Select(candidate => candidate.ToDto()).ToList();
        if (topCandidates.Count == 0)
        {
            AddWarning(report, "No tennis selections currently clear both the publish threshold and the live market edge floor across the SportyBet markets we could price today.");
        }

        return report;
    }

    private async Task<Dictionary<string, Prediction>> LoadCurrentPredictionLookupAsync(DateOnly localDate, CancellationToken ct)
    {
        var currentPredictions = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction => prediction.MatchLocalDate == localDate)
            .Where(prediction => prediction.IsCurrentRevision)
            .ToListAsync(ct);

        return currentPredictions
            .GroupBy(prediction => BuildCurrentPredictionLookupKey(
                FixtureIdentityFactory.FromPrediction(prediction).FixtureKey,
                prediction.PredictionCategory,
                prediction.PredictedOutcome),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(prediction => prediction.RevisionNumber)
                    .ThenByDescending(prediction => prediction.CreatedAt)
                    .First(),
                StringComparer.Ordinal);
    }

    private ThresholdDecision ResolveThresholdDecision(PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => _thresholdTuningService.GetThresholdDecision(PredictionMarket.HomeWin, _settings.HomeWinStrong),
            PredictionMarket.AwayWin => _thresholdTuningService.GetThresholdDecision(PredictionMarket.AwayWin, _settings.AwayWinStrong),
            PredictionMarket.Over25Sets => _thresholdTuningService.GetThresholdDecision(PredictionMarket.Over25Sets, _settings.OverTwoPointFiveSetsStrongThreshold),
            PredictionMarket.Under25Sets => _thresholdTuningService.GetThresholdDecision(PredictionMarket.Under25Sets, _settings.UnderTwoPointFiveSetsStrongThreshold),
            PredictionMarket.HomeSetHandicap => _thresholdTuningService.GetThresholdDecision(PredictionMarket.HomeSetHandicap, _settings.HomeSetHandicapStrongThreshold),
            PredictionMarket.AwaySetHandicap => _thresholdTuningService.GetThresholdDecision(PredictionMarket.AwaySetHandicap, _settings.AwaySetHandicapStrongThreshold),
            _ => new ThresholdDecision { Threshold = 1.0, ThresholdSource = "Unsupported" }
        };
    }

    private static Dictionary<string, int> CreateExclusionCountMap()
    {
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["unsupported_market"] = 0,
            ["no_source_fixture"] = 0,
            ["no_exact_line"] = 0,
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
            CreateExclusionStat(counts, "unsupported_market", "Unsupported market", "The forecast market is not part of the current tennis pricing scope."),
            CreateExclusionStat(counts, "no_source_fixture", "No source fixture", "No matching SportyBet tennis fixture could be aligned to the model fixture."),
            CreateExclusionStat(counts, "no_exact_line", "Exact line unavailable", "The SportyBet fixture existed, but this exact total-sets or set-handicap line was not priced today."),
            CreateExclusionStat(counts, "no_source_price", "No source price", "A matching source selection existed, but it did not expose a usable live probability or decimal odds."),
            CreateExclusionStat(counts, "below_threshold", "Below threshold", "The calibrated probability did not clear the publish threshold for that market."),
            CreateExclusionStat(counts, "insufficient_edge", "Below edge floor", "The model leaned the right way, but not enough above the market to count as value.")
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

    private static string BuildCandidateKey(string date, string kickoffTime, string league, string homeTeam, string awayTeam, string predictionCategory, string predictedOutcome)
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

    private static bool IsSupportedMarket(PredictionMarket market)
    {
        return market is
            PredictionMarket.HomeWin or
            PredictionMarket.AwayWin or
            PredictionMarket.Over25Sets or
            PredictionMarket.Under25Sets or
            PredictionMarket.HomeSetHandicap or
            PredictionMarket.AwaySetHandicap;
    }

    private static bool HasExactSourceSelection(SourceMarketFixture fixture, string predictionCategory, string predictedOutcome)
    {
        return fixture.MarketSelections.Any(selection =>
            string.Equals(NormalizeKeyPart(selection.Market), NormalizeKeyPart(predictionCategory), StringComparison.Ordinal) &&
            string.Equals(NormalizeKeyPart(selection.Prediction), NormalizeKeyPart(predictedOutcome), StringComparison.Ordinal));
    }

    private static string BuildMarketKey(string fixtureKey, string predictionCategory, string predictedOutcome)
    {
        return string.Join("|", NormalizeKeyPart(fixtureKey), NormalizeKeyPart(predictionCategory), NormalizeKeyPart(predictedOutcome));
    }

    private static string NormalizeKeyPart(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string BuildEdgeSource(double modelProbability, double marketProbability)
    {
        return $"Model {(modelProbability * 100):F1}% vs market {(marketProbability * 100):F1}%";
    }

    private static string BuildFallbackJustification(string predictedOutcome, double modelProbability, double decimalOdds, double edge, ThresholdDecision thresholdDecision)
    {
        return $"{predictedOutcome} is priced at {decimalOdds:0.00} with model confidence {(modelProbability * 100):F1}%. That is a {(edge * 100):F1}+ pt edge above market and it clears the {thresholdDecision.ThresholdSource.ToLowerInvariant()} {(thresholdDecision.Threshold * 100):F1}% threshold.";
    }

    private static void AddWarning(ValueBetReportDto report, string message)
    {
        if (report.Warnings.Contains(message, StringComparer.Ordinal))
        {
            return;
        }

        report.Warnings.Add(message);
    }

    private static string BuildCurrentPredictionLookupKey(string fixtureKey, string predictionCategory, string predictedOutcome)
    {
        return string.Join("|", fixtureKey.Trim(), predictionCategory.Trim(), predictedOutcome.Trim());
    }

    private sealed class ValueBetCandidate
    {
        public string CandidateKey { get; init; } = string.Empty;
        public string FixtureKey { get; init; } = string.Empty;
        public int? PredictionId { get; init; }
        public DateTime? MatchDateTimeUtc { get; init; }
        public string League { get; init; } = string.Empty;
        public string HomeTeam { get; init; } = string.Empty;
        public string AwayTeam { get; init; } = string.Empty;
        public string KickoffTime { get; init; } = string.Empty;
        public string PredictionCategory { get; init; } = string.Empty;
        public string PredictedOutcome { get; init; } = string.Empty;
        public double MathematicalProbability { get; init; }
        public double MarketProbability { get; init; }
        public double DecimalOdds { get; init; }
        public double ImpliedProbability { get; init; }
        public double ExpectedValuePercent { get; init; }
        public double Edge { get; init; }
        public double ThresholdUsed { get; init; }
        public string ThresholdSource { get; init; } = "Configured";
        public string CalibratorUsed { get; init; } = "Bucket";
        public string PricingSource { get; init; } = string.Empty;
        public string OddsFreshness { get; init; } = string.Empty;
        public string OddsDerivationSource { get; init; } = string.Empty;
        public string EdgeSource { get; init; } = string.Empty;
        public string AiJustification { get; init; } = string.Empty;

        public ValueBetDto ToDto()
        {
            return new ValueBetDto
            {
                PredictionId = PredictionId,
                MatchDateTimeUtc = MatchDateTimeUtc,
                League = League,
                HomeTeam = HomeTeam,
                AwayTeam = AwayTeam,
                KickoffTime = KickoffTime,
                PredictionCategory = PredictionCategory,
                PredictedOutcome = PredictedOutcome,
                MathematicalProbability = MathematicalProbability,
                MarketProbability = MarketProbability,
                DecimalOdds = DecimalOdds,
                ImpliedProbability = ImpliedProbability,
                ExpectedValuePercent = ExpectedValuePercent,
                Edge = Edge,
                ThresholdUsed = ThresholdUsed,
                ThresholdSource = ThresholdSource,
                CalibratorUsed = CalibratorUsed,
                PricingSource = PricingSource,
                OddsFreshness = OddsFreshness,
                OddsDerivationSource = OddsDerivationSource,
                EdgeSource = EdgeSource,
                AiJustification = AiJustification
            };
        }
    }
}
