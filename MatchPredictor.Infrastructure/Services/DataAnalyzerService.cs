using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Statistics;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace MatchPredictor.Infrastructure.Services;

public class DataAnalyzerService : IDataAnalyzerService
{
    private readonly IProbabilityCalculator _probabilityCalculator;
    private readonly ICalibrationService _calibrationService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly IProbabilityCorrectionService _probabilityCorrectionService;
    private readonly IStatisticalSignalProvider? _statisticalSignalProvider;
    private readonly IEnsembleWeightProvider? _ensembleWeightProvider;
    private readonly IMarketPredictionModelService? _marketPredictionModelService;
    private readonly EnsembleWeights _ensembleWeights;
    private readonly PredictionSettings _settings;

    public DataAnalyzerService(
        IProbabilityCalculator probabilityCalculator,
        ICalibrationService calibrationService,
        IThresholdTuningService thresholdTuningService,
        IProbabilityCorrectionService probabilityCorrectionService,
        IOptions<PredictionSettings> options,
        IStatisticalSignalProvider? statisticalSignalProvider = null,
        IEnsembleWeightProvider? ensembleWeightProvider = null,
        IMarketPredictionModelService? marketPredictionModelService = null)
    {
        _probabilityCalculator = probabilityCalculator;
        _calibrationService = calibrationService;
        _thresholdTuningService = thresholdTuningService;
        _probabilityCorrectionService = probabilityCorrectionService;
        _statisticalSignalProvider = statisticalSignalProvider;
        _ensembleWeightProvider = ensembleWeightProvider;
        _marketPredictionModelService = marketPredictionModelService;
        _settings = options.Value;
        // Signals: de-vigged bookmaker odds ("Bookmaker"), the sports-ai.dev feed as shaped
        // by ProbabilityCalculator ("Market"), and the Dixon-Coles + Elo statistical core
        // ("DixonColes"). These defaults apply until learned per-market stacking profiles
        // (EnsembleWeightProfiles) are promoted by the nightly learning loop.
        _ensembleWeights = EnsembleWeights.ProductionDefault;
    }

    private EnsembleWeights ResolveWeights(PredictionMarket market)
    {
        return _ensembleWeightProvider?.GetWeights(market, _ensembleWeights) ?? _ensembleWeights;
    }

    public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches)
    {
        return BuildForecastCandidates(matches, bookmakerSignals: null);
    }

    public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(
        IEnumerable<MatchData> matches,
        BookmakerSignalSet? bookmakerSignals)
    {
        var matchList = matches as IReadOnlyCollection<MatchData> ?? matches.ToList();
        var signalSet = _statisticalSignalProvider?.BuildSignals(matchList);

        return matchList
            .SelectMany(match => BuildForecastCandidatesForMatch(match, signalSet, bookmakerSignals))
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates)
    {
        var forecasts = forecastCandidates.ToList();
        var published = new List<PredictionCandidate>();
        foreach (var candidate in forecasts)
        {
            var fallbackThreshold = ResolveFallbackThreshold(candidate.Market);
            var leagueDecision = _thresholdTuningService.GetThresholdDecision(
                candidate.Market,
                fallbackThreshold,
                candidate.League);
            candidate.ThresholdUsed = leagueDecision.Threshold;
            candidate.ThresholdSource = leagueDecision.ThresholdSource;
        }

        published.AddRange(MarkPublished(forecasts.Where(candidate =>
            candidate.Market == PredictionMarket.BothTeamsScore &&
            HasExplicitBttsMarket(candidate) &&
            candidate.CalibratedProbability >= candidate.ThresholdUsed)));

        published.AddRange(MarkPublished(forecasts.Where(candidate =>
            candidate.Market == PredictionMarket.Draw &&
            candidate.CalibratedProbability >= candidate.ThresholdUsed)));

        foreach (var totalsGroup in forecasts
                     .Where(candidate => candidate.Market is PredictionMarket.Over25Goals or PredictionMarket.Under25Goals)
                     .GroupBy(candidate => (
                         candidate.MatchLocalDate,
                         candidate.HomeTeam,
                         candidate.AwayTeam,
                         candidate.League)))
        {
            var qualifiedTotals = totalsGroup
                .Where(candidate => candidate.CalibratedProbability >= candidate.ThresholdUsed)
                .OrderByDescending(candidate => candidate.CalibratedProbability)
                .ThenByDescending(candidate => candidate.Market == PredictionMarket.Over25Goals ? 1 : 0)
                .FirstOrDefault();

            if (qualifiedTotals is not null)
            {
                qualifiedTotals.WasPublished = true;
                published.Add(qualifiedTotals);
            }
        }

        foreach (var matchGroup in forecasts
                     .Where(candidate => candidate.Market is PredictionMarket.HomeWin or PredictionMarket.AwayWin)
                     .GroupBy(candidate => (
                         candidate.MatchLocalDate,
                         candidate.HomeTeam,
                         candidate.AwayTeam,
                         candidate.League)))
        {
            var bestSide = matchGroup
                .OrderByDescending(candidate => candidate.CalibratedProbability)
                .First();

            if (bestSide.CalibratedProbability >= bestSide.ThresholdUsed)
            {
                bestSide.WasPublished = true;
                published.Add(bestSide);
            }
        }

        return published;
    }

    private double ResolveFallbackThreshold(PredictionMarket market) =>
        market switch
        {
            PredictionMarket.BothTeamsScore => _settings.BttsScoreThreshold,
            PredictionMarket.Over25Goals => _settings.OverTwoGoalsStrongThreshold,
            PredictionMarket.Under25Goals => _settings.UnderTwoGoalsStrongThreshold,
            PredictionMarket.HomeWin => _settings.HomeWinStrong,
            PredictionMarket.AwayWin => _settings.AwayWinStrong,
            PredictionMarket.Draw => _settings.DrawStrongThreshold,
            _ => 0.5
        };

    public IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market == PredictionMarket.BothTeamsScore)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market == PredictionMarket.Over25Goals)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> UnderTwoGoals(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market == PredictionMarket.Under25Goals)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market is PredictionMarket.HomeWin or PredictionMarket.AwayWin)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    private PredictionCandidate? BuildCandidate(MatchData match, PredictionMarket market, string predictedOutcome, double rawProbability)
    {
        if (!HasRequiredTeams(match) || rawProbability <= 0)
            return null;

        var correctedProbability = _probabilityCorrectionService.ApplyCorrection(market, rawProbability);
        var league = match.League?.Trim();
        var calibration = _calibrationService.CalibrateWithDecision(market, correctedProbability, league);
        return CreateCandidate(
            match,
            market,
            predictedOutcome,
            rawProbability,
            correctedProbability,
            calibration.Probability,
            calibration.CalibratorUsed);
    }

    private static PredictionCandidate CreateCandidate(
        MatchData match,
        PredictionMarket market,
        string predictedOutcome,
        double rawProbability,
        double correctedProbability,
        double calibratedProbability,
        string calibratorUsed)
    {
        DateTime? utcDateTime = match.MatchDateTime;
        var matchLocalDate = match.MatchLocalDate;
        var matchLocalTime = match.MatchLocalTime;
        var date = matchLocalDate.HasValue
            ? DateTimeProvider.FormatLocalDate(matchLocalDate.Value)
            : match.Date?.Trim() ?? string.Empty;
        var time = matchLocalTime.HasValue
            ? DateTimeProvider.FormatLocalTime(matchLocalTime.Value)
            : match.Time?.Trim() ?? string.Empty;

        if (utcDateTime is null && matchLocalDate.HasValue)
        {
            var localDateTime = matchLocalDate.Value.ToDateTime(matchLocalTime ?? new TimeOnly(0, 0), DateTimeKind.Unspecified);
            utcDateTime = DateTimeProvider.ConvertLocalToUtc(localDateTime);
        }
        else if (utcDateTime is null)
        {
            var normalizedDateTime = DateTimeProvider.ParseCanonicalMatchDateTime(match.Date, match.Time);
            date = DateTimeProvider.FormatLocalDate(normalizedDateTime.localDate);
            time = DateTimeProvider.FormatLocalTime(normalizedDateTime.localTime);
            utcDateTime = normalizedDateTime.utcDateTime;
            matchLocalDate = normalizedDateTime.localDate;
            matchLocalTime = normalizedDateTime.localTime;
        }
        else
        {
            matchLocalDate ??= DateTimeProvider.ConvertUtcToLocalDate(utcDateTime.Value);
            matchLocalTime ??= DateTimeProvider.ConvertUtcToLocalTime(utcDateTime.Value);
            date = DateTimeProvider.FormatLocalDate(matchLocalDate.Value);
            time = matchLocalTime.HasValue ? DateTimeProvider.FormatLocalTime(matchLocalTime.Value) : time;
        }

        return new PredictionCandidate
        {
            Market = market,
            Date = date,
            Time = time,
            MatchLocalDate = matchLocalDate ?? DateOnly.ParseExact(date, "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture),
            MatchLocalTime = matchLocalTime,
            MatchDateTime = utcDateTime,
            FixtureKey = string.Empty,
            League = match.League?.Trim() ?? string.Empty,
            HomeTeam = match.HomeTeam?.Trim() ?? string.Empty,
            AwayTeam = match.AwayTeam?.Trim() ?? string.Empty,
            PredictionCategory = market.ToCategory(),
            PredictedOutcome = predictedOutcome,
            RawProbability = Math.Clamp(rawProbability, 0.0, 1.0),
            CorrectedProbability = Math.Clamp(correctedProbability, 0.0, 1.0),
            CalibratedProbability = Math.Clamp(calibratedProbability, 0.0, 1.0),
            CalibratorUsed = calibratorUsed,
            FeatureContributionsJson = "{}"
        };
    }

    private static bool HasExplicitBttsMarket(PredictionCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.FeatureContributionsJson) ||
            candidate.FeatureContributionsJson == "{}")
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(candidate.FeatureContributionsJson);
            if (document.RootElement.TryGetProperty("explicitBttsMarket", out var flag) &&
                flag.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                return true;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<PredictionCandidate> MarkPublished(IEnumerable<PredictionCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            candidate.WasPublished = true;
            yield return candidate;
        }
    }

    private static bool HasRequiredTeams(MatchData match)
    {
        return !string.IsNullOrWhiteSpace(match.HomeTeam) && !string.IsNullOrWhiteSpace(match.AwayTeam);
    }

    private IEnumerable<PredictionCandidate> BuildForecastCandidatesForMatch(
        MatchData match,
        IStatisticalSignalSet? signalSet,
        BookmakerSignalSet? bookmakerSignals)
    {
        var marketProbabilities = _probabilityCalculator.CalculateProbabilities(match);
        var statisticalSignal = signalSet?.GetSignal(match);
        var bookmakerSignal = bookmakerSignals?.GetSignal(match);
        // With a single signal the logit blend is the identity, so skip the work.
        var probabilities = statisticalSignal is null && bookmakerSignal is null && _marketPredictionModelService is null
            ? marketProbabilities
            : BlendWithPerMarketWeights(match, marketProbabilities, statisticalSignal, bookmakerSignal);

        var candidates = new[]
        {
            BuildCandidate(
                match,
                PredictionMarket.BothTeamsScore,
                "BTTS",
                probabilities.Btts),
            BuildCandidate(
                match,
                PredictionMarket.Over25Goals,
                "Over 2.5",
                probabilities.Over25),
            BuildCandidate(
                match,
                PredictionMarket.Under25Goals,
                "Under 2.5",
                probabilities.Under25),
            BuildCandidate(
                match,
                PredictionMarket.HomeWin,
                "Home Win",
                probabilities.HomeWin),
            BuildCandidate(
                match,
                PredictionMarket.AwayWin,
                "Away Win",
                probabilities.AwayWin),
            BuildCandidate(
                match,
                PredictionMarket.Draw,
                "Draw",
                probabilities.Draw)
        };

        var realizedCandidates = candidates
            .Where(candidate => candidate != null)
            .Cast<PredictionCandidate>()
            .ToList();

        foreach (var candidate in realizedCandidates)
        {
            candidate.FeatureContributionsJson = BuildFeatureContributionSummary(
                match, probabilities, marketProbabilities, statisticalSignal, bookmakerSignal, candidate.Market);
        }

        return realizedCandidates;
    }

    /// <summary>
    /// Blends the three signals per market, honoring learned per-market stacking weights.
    /// The 1X2 triple is renormalized to sum to 1 and Under 2.5 is the complement of
    /// Over 2.5 (mirroring <see cref="EnsembleProbabilityBlender.Blend(MatchProbabilities?, MatchProbabilities?, MatchProbabilities?, MatchProbabilities?, EnsembleWeights?)"/>).
    /// </summary>
    private MatchProbabilities BlendWithPerMarketWeights(
        MatchData match,
        MatchProbabilities calculator,
        MatchProbabilities? statistical,
        PartialMatchProbabilities? bookmaker)
    {
        double BlendFor(PredictionMarket market, double? bookmakerValue, double calculatorValue, double? statisticalValue)
        {
            var weights = ResolveWeights(market);
            var mlValue = _marketPredictionModelService?.TryPredict(match, market, calculatorValue, statisticalValue, bookmakerValue);
            return EnsembleProbabilityBlender.BlendLogit(
                (bookmakerValue, weights.Bookmaker),
                (calculatorValue, weights.Market),
                (statisticalValue, weights.DixonColes),
                (mlValue, weights.Ml));
        }

        var homeWin = BlendFor(PredictionMarket.HomeWin, bookmaker?.HomeWin, calculator.HomeWin, statistical?.HomeWin);
        var draw = BlendFor(PredictionMarket.Draw, bookmaker?.Draw, calculator.Draw, statistical?.Draw);
        var awayWin = BlendFor(PredictionMarket.AwayWin, bookmaker?.AwayWin, calculator.AwayWin, statistical?.AwayWin);
        var over25 = BlendFor(PredictionMarket.Over25Goals, bookmaker?.Over25, calculator.Over25, statistical?.Over25);
        var btts = BlendFor(PredictionMarket.BothTeamsScore, bookmaker?.Btts, calculator.Btts, statistical?.Btts);

        var total = homeWin + draw + awayWin;
        if (total > 0)
        {
            homeWin /= total;
            draw /= total;
            awayWin /= total;
        }

        return new MatchProbabilities(
            Btts: Math.Clamp(btts, 0.0, 1.0),
            Over25: Math.Clamp(over25, 0.0, 1.0),
            Under25: Math.Clamp(1.0 - over25, 0.0, 1.0),
            Draw: Math.Clamp(draw, 0.0, 1.0),
            HomeWin: Math.Clamp(homeWin, 0.0, 1.0),
            AwayWin: Math.Clamp(awayWin, 0.0, 1.0));
    }

    private string BuildFeatureContributionSummary(
        MatchData match,
        MatchProbabilities probabilities,
        MatchProbabilities calculatorSignal,
        MatchProbabilities? statisticalSignal,
        PartialMatchProbabilities? bookmakerSignal,
        PredictionMarket market)
    {
        var oneX2Available = match.TryGetNormalizedOneX2(out var oneX2);
        var over25Available = match.TryGetNormalizedOver25Pair(out var over25Pair);
        var bttsAvailable = match.TryGetNormalizedBttsPair(out var bttsPair);
        var mlSignal = _marketPredictionModelService?.TryPredict(
            match,
            market,
            GetMarketProbability(calculatorSignal, market),
            statisticalSignal is null ? null : GetMarketProbability(statisticalSignal, market),
            bookmakerSignal is null ? null : GetMarketProbability(bookmakerSignal, market));

        var summary = new Dictionary<string, object?>
        {
            ["market"] = market.ToString(),
            ["sourceSignals"] = new Dictionary<string, double?>
            {
                ["homeWin"] = oneX2Available ? oneX2.home : null,
                ["draw"] = oneX2Available ? oneX2.draw : null,
                ["awayWin"] = oneX2Available ? oneX2.away : null,
                ["over25"] = over25Available ? over25Pair.over25 : null,
                ["bttsYes"] = bttsAvailable ? bttsPair.yes : null
            },
            ["modelOutputs"] = new Dictionary<string, double>
            {
                ["btts"] = probabilities.Btts,
                ["over25"] = probabilities.Over25,
                ["under25"] = probabilities.Under25,
                ["homeWin"] = probabilities.HomeWin,
                ["awayWin"] = probabilities.AwayWin,
                ["draw"] = probabilities.Draw
            },
            ["statisticalSignal"] = statisticalSignal is null
                ? null
                : BuildProbabilitySignalMap(
                    statisticalSignal.Btts,
                    statisticalSignal.Over25,
                    statisticalSignal.Under25,
                    statisticalSignal.HomeWin,
                    statisticalSignal.AwayWin,
                    statisticalSignal.Draw),
            ["statisticalSignalApplied"] = statisticalSignal is not null,
            ["calculatorSignal"] = BuildProbabilitySignalMap(
                calculatorSignal.Btts,
                calculatorSignal.Over25,
                calculatorSignal.Under25,
                calculatorSignal.HomeWin,
                calculatorSignal.AwayWin,
                calculatorSignal.Draw),
            ["bookmakerSignal"] = bookmakerSignal is null
                ? null
                : BuildProbabilitySignalMap(
                    bookmakerSignal.Btts,
                    bookmakerSignal.Over25,
                    bookmakerSignal.Under25,
                    bookmakerSignal.HomeWin,
                    bookmakerSignal.AwayWin,
                    bookmakerSignal.Draw),
            ["bookmakerSignalApplied"] = bookmakerSignal is not null,
            ["mlSignal"] = mlSignal,
            ["mlSignalApplied"] = mlSignal is not null,
            ["explicitBttsMarket"] = match.TryGetNormalizedBttsPair(out _) || bookmakerSignal?.Btts is > 0
        };

        return JsonSerializer.Serialize(summary);
    }

    private static Dictionary<string, double?> BuildProbabilitySignalMap(
        double? btts,
        double? over25,
        double? under25,
        double? homeWin,
        double? awayWin,
        double? draw)
    {
        var resolvedUnder25 = under25 ?? (over25 is double over ? 1.0 - over : null);
        return new Dictionary<string, double?>
        {
            ["btts"] = btts,
            ["over25"] = over25,
            ["under25"] = resolvedUnder25,
            ["homeWin"] = homeWin,
            ["awayWin"] = awayWin,
            ["draw"] = draw
        };
    }

    private static double GetMarketProbability(MatchProbabilities probabilities, PredictionMarket market) =>
        market switch
        {
            PredictionMarket.BothTeamsScore => probabilities.Btts,
            PredictionMarket.Over25Goals => probabilities.Over25,
            PredictionMarket.Under25Goals => probabilities.Under25,
            PredictionMarket.HomeWin => probabilities.HomeWin,
            PredictionMarket.AwayWin => probabilities.AwayWin,
            PredictionMarket.Draw => probabilities.Draw,
            _ => 0.0
        };

    private static double? GetMarketProbability(PartialMatchProbabilities probabilities, PredictionMarket market) =>
        market switch
        {
            PredictionMarket.BothTeamsScore => probabilities.Btts,
            PredictionMarket.Over25Goals => probabilities.Over25,
            PredictionMarket.Under25Goals => probabilities.Under25 ?? (probabilities.Over25 is double over ? 1.0 - over : null),
            PredictionMarket.HomeWin => probabilities.HomeWin,
            PredictionMarket.AwayWin => probabilities.AwayWin,
            PredictionMarket.Draw => probabilities.Draw,
            _ => null
        };
}
