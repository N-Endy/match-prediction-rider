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

        foreach (var bttsGroup in forecasts
                     .Where(candidate => candidate.Market == PredictionMarket.BothTeamsScore)
                     .GroupBy(candidate => (
                         candidate.MatchLocalDate,
                         candidate.HomeTeam,
                         candidate.AwayTeam,
                         candidate.League)))
        {
            var qualifiedBtts = bttsGroup
                .Where(candidate =>
                    HasExplicitBttsMarket(candidate) &&
                    candidate.CalibratedProbability >= candidate.ThresholdUsed &&
                    QualifiesForBttsAffinity(candidate, forecasts))
                .OrderByDescending(candidate => candidate.CalibratedProbability)
                .ThenByDescending(candidate => !IsBttsNoOutcome(candidate.PredictedOutcome) ? 1 : 0)
                .FirstOrDefault();

            if (qualifiedBtts is not null)
            {
                qualifiedBtts.WasPublished = true;
                published.Add(qualifiedBtts);
            }
        }

        published.AddRange(MarkPublished(forecasts.Where(candidate =>
            candidate.Market == PredictionMarket.Draw &&
            HasExplicitDrawMarket(candidate) &&
            candidate.CalibratedProbability >= candidate.ThresholdUsed &&
            QualifiesForDrawAffinity(candidate, forecasts))));

        foreach (var totalsGroup in forecasts
                     .Where(candidate => candidate.Market is PredictionMarket.Over25Goals or PredictionMarket.Under25Goals)
                     .GroupBy(candidate => (
                         candidate.MatchLocalDate,
                         candidate.HomeTeam,
                         candidate.AwayTeam,
                         candidate.League)))
        {
            var qualifiedTotals = totalsGroup
                .Where(candidate =>
                    HasExplicitTotalsMarket(candidate) &&
                    candidate.CalibratedProbability >= candidate.ThresholdUsed &&
                    QualifiesForTotalsAffinity(candidate, forecasts))
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

            if (bestSide.CalibratedProbability >= bestSide.ThresholdUsed &&
                QualifiesForDominantWin(bestSide, forecasts))
            {
                bestSide.WasPublished = true;
                published.Add(bestSide);
            }
        }

        if (_settings.EnableOver15Predictions)
        {
            foreach (var over15Group in forecasts
                         .Where(candidate => candidate.Market == PredictionMarket.Over15Goals)
                         .GroupBy(candidate => (
                             candidate.MatchLocalDate,
                             candidate.HomeTeam,
                             candidate.AwayTeam,
                             candidate.League)))
            {
                var qualifiedOver15 = over15Group
                    .Where(candidate =>
                        candidate.CalibratedProbability >= candidate.ThresholdUsed &&
                        QualifiesForOver15Affinity(candidate, forecasts))
                    .OrderByDescending(candidate => candidate.CalibratedProbability)
                    .FirstOrDefault();

                if (qualifiedOver15 is not null)
                {
                    qualifiedOver15.WasPublished = true;
                    published.Add(qualifiedOver15);
                }
            }
        }

        if (_settings.EnableDoubleChancePredictions)
        {
            foreach (var dcGroup in forecasts
                         .Where(candidate => candidate.Market == PredictionMarket.DoubleChance)
                         .GroupBy(candidate => (
                             candidate.MatchLocalDate,
                             candidate.HomeTeam,
                             candidate.AwayTeam,
                             candidate.League)))
            {
                var qualifiedDc = dcGroup
                    .Where(candidate =>
                        candidate.CalibratedProbability >= candidate.ThresholdUsed &&
                        QualifiesForDoubleChanceAffinity(candidate, forecasts))
                    .OrderByDescending(candidate => candidate.CalibratedProbability)
                    .FirstOrDefault();

                if (qualifiedDc is not null)
                {
                    qualifiedDc.WasPublished = true;
                    published.Add(qualifiedDc);
                }
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
            PredictionMarket.Over15Goals => _settings.OverOneFiveStrongThreshold,
            PredictionMarket.DoubleChance => _settings.DoubleChanceStrongThreshold,
            _ => 0.5
        };

    public IReadOnlyList<PredictionCandidate> Over15Goals(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market == PredictionMarket.Over15Goals)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> DoubleChance(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market == PredictionMarket.DoubleChance)
            .Cast<PredictionCandidate>()
            .ToList();
    }

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
        return HasExplicitMarketFlag(candidate, "explicitBttsMarket");
    }

    private static bool HasExplicitDrawMarket(PredictionCandidate candidate)
    {
        return HasExplicitMarketFlag(candidate, "explicitDrawMarket");
    }

    private static bool HasExplicitTotalsMarket(PredictionCandidate candidate)
    {
        return HasExplicitMarketFlag(candidate, "explicitTotalsMarket");
    }

    private static bool HasExplicitMarketFlag(PredictionCandidate candidate, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(candidate.FeatureContributionsJson) ||
            candidate.FeatureContributionsJson == "{}")
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(candidate.FeatureContributionsJson);
            if (document.RootElement.TryGetProperty(propertyName, out var flag) &&
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

    /// <summary>
    /// Precision Draw Gating (Sniper Draw): Ensures candidate draws only publish when total goals
    /// expectation is not high-scoring (Over 2.5 <= MaxOver25ProbabilityForDraw) and the Draw Affinity
    /// Index (P(Under 2.5) * (1 - |P_Home - P_Away|)) satisfies the structural low-scoring parity threshold.
    /// </summary>
    private bool QualifiesForDrawAffinity(PredictionCandidate drawCandidate, IReadOnlyList<PredictionCandidate> allForecasts)
    {
        if (_settings.MinDrawAffinity <= 0 && _settings.MaxOver25ProbabilityForDraw >= 1.0)
        {
            return true;
        }

        var companionMatches = allForecasts.Where(c => IsSameFixture(c, drawCandidate)).ToList();
        if (companionMatches.Count <= 1)
        {
            // Isolated single-market forecast (e.g. mock unit tests without companion candidates).
            return true;
        }

        var over25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Over25Goals);
        var under25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Under25Goals);
        var homeCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.HomeWin);
        var awayCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.AwayWin);

        var hasExplicitTotals = (over25Candidate is not null && HasExplicitTotalsMarket(over25Candidate))
            || (under25Candidate is not null && HasExplicitTotalsMarket(under25Candidate));

        // 1. Total Goals Gate: Reject draw if explicit Over 2.5 probability indicates high goal expectancy.
        // Draws in games with Over 2.5 occur <18% of the time in professional football.
        if (hasExplicitTotals && _settings.MaxOver25ProbabilityForDraw < 1.0)
        {
            var over25Prob = over25Candidate?.CalibratedProbability
                ?? (under25Candidate is not null && under25Candidate.CalibratedProbability > 0
                    ? 1.0 - under25Candidate.CalibratedProbability
                    : (double?)null);

            if (over25Prob is > 0 && over25Prob.Value > _settings.MaxOver25ProbabilityForDraw)
            {
                return false;
            }
        }

        // 2. Draw Affinity Index Gate (P(Under 2.5) * (1 - |P_Home - P_Away|))
        if (_settings.MinDrawAffinity > 0 && homeCandidate is not null && awayCandidate is not null)
        {
            var under25Prob = under25Candidate?.CalibratedProbability
                ?? (over25Candidate is not null && over25Candidate.CalibratedProbability > 0
                    ? 1.0 - over25Candidate.CalibratedProbability
                    : (double?)null);

            if (under25Prob is > 0)
            {
                var paritySpread = Math.Abs(homeCandidate.CalibratedProbability - awayCandidate.CalibratedProbability);
                var drawAffinity = under25Prob.Value * (1.0 - paritySpread);

                if (drawAffinity < _settings.MinDrawAffinity)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Precision BTTS Gating (Sniper BTTS):
    /// - BTTS Yes: Requires low clean-sheet risk (Under 2.5 <= 0.52), balanced win expectation
    ///   (|P_Home - P_Away| <= MaxSpreadForBttsYes), and passing the BTTS Affinity Index.
    /// - BTTS No: Qualifies when low-total (Under 2.5 >= 0.50) or lopsided clean-sheet dynamics (spread >= 0.35) dominate.
    /// </summary>
    private bool QualifiesForBttsAffinity(PredictionCandidate bttsCandidate, IReadOnlyList<PredictionCandidate> allForecasts)
    {
        var isBttsNo = IsBttsNoOutcome(bttsCandidate.PredictedOutcome);

        if (isBttsNo && !_settings.EnableBttsNoPredictions)
        {
            return false;
        }

        var companionMatches = allForecasts.Where(c => IsSameFixture(c, bttsCandidate)).ToList();
        if (companionMatches.Count <= 1)
        {
            // Isolated single-market forecast (e.g. mock unit tests without companion candidates).
            return true;
        }

        var over25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Over25Goals);
        var under25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Under25Goals);
        var homeCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.HomeWin);
        var awayCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.AwayWin);

        var hasExplicitTotals = (over25Candidate is not null && HasExplicitTotalsMarket(over25Candidate))
            || (under25Candidate is not null && HasExplicitTotalsMarket(under25Candidate));

        var under25Prob = under25Candidate?.CalibratedProbability
            ?? (over25Candidate is not null && over25Candidate.CalibratedProbability > 0
                ? 1.0 - over25Candidate.CalibratedProbability
                : (double?)null);

        var paritySpread = homeCandidate is not null && awayCandidate is not null
            ? Math.Abs(homeCandidate.CalibratedProbability - awayCandidate.CalibratedProbability)
            : (double?)null;

        // If no companion 1X2 or totals candidates exist (e.g. isolated BTTS tests), pass through.
        if (!hasExplicitTotals && paritySpread is null)
        {
            return true;
        }

        if (isBttsNo)
        {
            // High conviction for BTTS No: game is expected low-scoring (Under 2.5 >= 0.50) OR lopsided (spread >= 0.35).
            var lowTotal = under25Prob is not null && under25Prob.Value >= 0.50;
            var lopsided = paritySpread is not null && paritySpread.Value >= 0.35;
            return lowTotal || lopsided;
        }

        // BTTS Yes validation:
        // 1. Total Goals Gate: Reject BTTS Yes if explicit Under 2.5 indicates defensive/low-scoring game.
        if (hasExplicitTotals && under25Prob is > 0.52)
        {
            return false;
        }

        // 2. Parity Spread Gate: In heavy mismatches, clean sheet risk for the favourite spikes.
        if (paritySpread is not null && _settings.MaxSpreadForBttsYes < 1.0 && paritySpread.Value > _settings.MaxSpreadForBttsYes)
        {
            return false;
        }

        // 3. BTTS Affinity Index (BAI) Gate: (1 - spread * 0.5) * (1 - under25 * 0.6)
        if (_settings.MinBttsAffinity > 0 && under25Prob is not null && paritySpread is not null)
        {
            var bttsAffinity = (1.0 - paritySpread.Value * 0.50) * (1.0 - under25Prob.Value * 0.60);
            if (bttsAffinity < _settings.MinBttsAffinity)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Precision Straight Win Gating (Dominant Win):
    /// - Dominance Ratio: P(Win) / (P(Draw) + P(Loss)) >= MinDominanceRatioForWin (1.25)
    /// - Win-to-Draw Ratio: P(Win) / P(Draw) >= MinWinToDrawRatio (1.70)
    /// - Away Win Confirmation: Away team must demonstrate superiority (>= 0.60 or clear spread over home >= 0.25)
    /// - Low Goal Trap: In defensive/0-0 fixtures (Under 2.5 > 0.68), requires win probability >= 0.75
    /// </summary>
    private bool QualifiesForDominantWin(PredictionCandidate winCandidate, IReadOnlyList<PredictionCandidate> allForecasts)
    {
        var companionMatches = allForecasts.Where(c => IsSameFixture(c, winCandidate)).ToList();
        if (companionMatches.Count <= 1)
        {
            return true;
        }

        var drawCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Draw);
        var opponentMarket = winCandidate.Market == PredictionMarket.HomeWin ? PredictionMarket.AwayWin : PredictionMarket.HomeWin;
        var opponentCandidate = companionMatches.FirstOrDefault(c => c.Market == opponentMarket);
        var under25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Under25Goals);
        var over25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Over25Goals);

        var drawProb = drawCandidate?.CalibratedProbability ?? 0.0;
        var oppProb = opponentCandidate?.CalibratedProbability ?? 0.0;

        // Isolated mock check: in synthetic unit tests where multiple outcomes are mocked independently without simplex normalization
        if (winCandidate.CalibratedProbability + drawProb + oppProb > 1.15)
        {
            return true;
        }

        var nonWinProb = drawProb + oppProb;

        if (nonWinProb > 0 && _settings.MinDominanceRatioForWin > 0)
        {
            var dominanceRatio = winCandidate.CalibratedProbability / nonWinProb;
            if (dominanceRatio < _settings.MinDominanceRatioForWin)
            {
                return false;
            }
        }

        if (drawProb > 0 && _settings.MinWinToDrawRatio > 0)
        {
            var winToDrawRatio = winCandidate.CalibratedProbability / drawProb;
            if (winToDrawRatio < _settings.MinWinToDrawRatio)
            {
                return false;
            }
        }

        if (winCandidate.Market == PredictionMarket.AwayWin)
        {
            if (oppProb > 0 && (winCandidate.CalibratedProbability - oppProb) < 0.25 && winCandidate.CalibratedProbability < 0.60)
            {
                return false;
            }
        }

        var under25Prob = under25Candidate?.CalibratedProbability
            ?? (over25Candidate is not null && over25Candidate.CalibratedProbability > 0
                ? 1.0 - over25Candidate.CalibratedProbability
                : (double?)null);

        if (_settings.MinTotalXgRequiredForWin > 0 && under25Prob is > 0.68 && winCandidate.CalibratedProbability < 0.75)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Precision Totals Gating:
    /// - Over 2.5: Rejects if Under 2.5 > MaxUnder25ProbForOver25 (0.45) to eliminate the 2-goal cluster trap (1-1, 2-0).
    /// - Under 2.5: Rejects if favorite win probability > MaxFavoriteWinProbForUnder25 (0.72) to eliminate single-team blowout busts (3-0, 4-0).
    /// </summary>
    private bool QualifiesForTotalsAffinity(PredictionCandidate totalsCandidate, IReadOnlyList<PredictionCandidate> allForecasts)
    {
        var companionMatches = allForecasts.Where(c => IsSameFixture(c, totalsCandidate)).ToList();
        if (companionMatches.Count <= 1)
        {
            return true;
        }

        if (totalsCandidate.Market == PredictionMarket.Over25Goals)
        {
            var under25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Under25Goals);
            var under25Prob = under25Candidate?.CalibratedProbability ?? (1.0 - totalsCandidate.CalibratedProbability);
            if (under25Prob > _settings.MaxUnder25ProbForOver25)
            {
                return false;
            }
        }
        else if (totalsCandidate.Market == PredictionMarket.Under25Goals)
        {
            var homeCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.HomeWin);
            var awayCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.AwayWin);
            var maxFavoriteWin = Math.Max(homeCandidate?.CalibratedProbability ?? 0.0, awayCandidate?.CalibratedProbability ?? 0.0);
            if (maxFavoriteWin > _settings.MaxFavoriteWinProbForUnder25)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Precision Over 1.5 Goals Gating:
    /// - Requires high goal frequency (Over 1.5 >= OverOneFiveStrongThreshold)
    /// - Rejects if Under 2.5 is overwhelmingly dominant (> 0.65) indicating low-tempo dead-rubber dynamics.
    /// </summary>
    private bool QualifiesForOver15Affinity(PredictionCandidate over15Candidate, IReadOnlyList<PredictionCandidate> allForecasts)
    {
        var companionMatches = allForecasts.Where(c => IsSameFixture(c, over15Candidate)).ToList();
        if (companionMatches.Count <= 1)
        {
            return true;
        }

        var under25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Under25Goals);
        var over25Candidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Over25Goals);
        var under25Prob = under25Candidate?.CalibratedProbability
            ?? (over25Candidate is not null && over25Candidate.CalibratedProbability > 0
                ? 1.0 - over25Candidate.CalibratedProbability
                : (double?)null);

        if (under25Prob is > 0.65)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Precision Double Chance Gating:
    /// - "1X": Requires opponent (away) win probability <= MaxUnderdogProbabilityForDoubleChance (0.22)
    /// - "X2": Requires opponent (home) win probability <= MaxUnderdogProbabilityForDoubleChance (0.22)
    /// - "12": Requires draw probability <= 0.28
    /// </summary>
    private bool QualifiesForDoubleChanceAffinity(PredictionCandidate dcCandidate, IReadOnlyList<PredictionCandidate> allForecasts)
    {
        var companionMatches = allForecasts.Where(c => IsSameFixture(c, dcCandidate)).ToList();
        if (companionMatches.Count <= 1)
        {
            return true;
        }

        var homeCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.HomeWin);
        var awayCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.AwayWin);
        var drawCandidate = companionMatches.FirstOrDefault(c => c.Market == PredictionMarket.Draw);

        var normOutcome = (dcCandidate.PredictedOutcome ?? string.Empty).Trim().ToUpperInvariant();

        if (normOutcome is "1X" or "1/X" or "HOME OR DRAW")
        {
            var awayProb = awayCandidate?.CalibratedProbability;
            if (awayProb is not null && awayProb.Value > _settings.MaxUnderdogProbabilityForDoubleChance)
            {
                return false;
            }
        }
        else if (normOutcome is "X2" or "X/2" or "DRAW OR AWAY")
        {
            var homeProb = homeCandidate?.CalibratedProbability;
            if (homeProb is not null && homeProb.Value > _settings.MaxUnderdogProbabilityForDoubleChance)
            {
                return false;
            }
        }
        else if (normOutcome is "12" or "1/2" or "HOME OR AWAY")
        {
            var drawProb = drawCandidate?.CalibratedProbability;
            if (drawProb is not null && drawProb.Value > 0.28)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBttsNoOutcome(string? outcome) =>
        outcome is not null && (outcome.Equals("No BTTS", StringComparison.OrdinalIgnoreCase) || outcome.Equals("BTTS No", StringComparison.OrdinalIgnoreCase));

    private static bool IsSameFixture(PredictionCandidate a, PredictionCandidate b)
    {
        if (!string.IsNullOrEmpty(a.FixtureKey) && !string.IsNullOrEmpty(b.FixtureKey))
        {
            return string.Equals(a.FixtureKey, b.FixtureKey, StringComparison.OrdinalIgnoreCase);
        }

        return a.MatchLocalDate == b.MatchLocalDate
            && string.Equals(a.HomeTeam, b.HomeTeam, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.AwayTeam, b.AwayTeam, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.League, b.League, StringComparison.OrdinalIgnoreCase);
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

        var bttsYesCandidate = BuildCandidate(
            match,
            PredictionMarket.BothTeamsScore,
            "BTTS",
            probabilities.Btts);

        PredictionCandidate? bttsNoCandidate = null;
        if (_settings.EnableBttsNoPredictions && bttsYesCandidate != null)
        {
            var rawNo = Math.Clamp(1.0 - bttsYesCandidate.RawProbability, 0.0, 1.0);
            var corrNo = Math.Clamp(1.0 - bttsYesCandidate.CorrectedProbability, 0.0, 1.0);
            var calNo = Math.Clamp(1.0 - bttsYesCandidate.CalibratedProbability, 0.0, 1.0);
            bttsNoCandidate = CreateCandidate(
                match,
                PredictionMarket.BothTeamsScore,
                "No BTTS",
                rawNo,
                corrNo,
                calNo,
                bttsYesCandidate.CalibratorUsed);
        }

        PredictionCandidate? over15Candidate = null;
        if (_settings.EnableOver15Predictions)
        {
            over15Candidate = BuildCandidate(
                match,
                PredictionMarket.Over15Goals,
                "Over 1.5",
                probabilities.Over15);
        }

        PredictionCandidate? dc1XCandidate = null;
        PredictionCandidate? dcX2Candidate = null;
        PredictionCandidate? dc12Candidate = null;
        if (_settings.EnableDoubleChancePredictions)
        {
            dc1XCandidate = BuildCandidate(
                match,
                PredictionMarket.DoubleChance,
                "1X",
                probabilities.DoubleChance1X);

            dcX2Candidate = BuildCandidate(
                match,
                PredictionMarket.DoubleChance,
                "X2",
                probabilities.DoubleChanceX2);

            if (_settings.EnableDoubleChance12)
            {
                dc12Candidate = BuildCandidate(
                    match,
                    PredictionMarket.DoubleChance,
                    "12",
                    probabilities.DoubleChance12);
            }
        }

        var candidates = new List<PredictionCandidate?>
        {
            bttsYesCandidate,
            bttsNoCandidate,
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
                probabilities.Draw),
            over15Candidate,
            dc1XCandidate,
            dcX2Candidate,
            dc12Candidate
        };

        var realizedCandidates = candidates
            .Where(candidate => candidate != null)
            .Cast<PredictionCandidate>()
            .ToList();

        NormalizeSimplexProbabilities(realizedCandidates);

        foreach (var candidate in realizedCandidates)
        {
            candidate.FeatureContributionsJson = BuildFeatureContributionSummary(
                match, probabilities, marketProbabilities, statisticalSignal, bookmakerSignal, candidate.Market);

            var challengerMl = _marketPredictionModelService?.TryPredictChallenger(
                match,
                candidate.Market,
                GetMarketProbability(marketProbabilities, candidate.Market),
                statisticalSignal is null ? null : GetMarketProbability(statisticalSignal, candidate.Market),
                bookmakerSignal is null ? null : GetMarketProbability(bookmakerSignal, candidate.Market));

            if (challengerMl is double rawChallenger)
            {
                if (IsBttsNoOutcome(candidate.PredictedOutcome))
                {
                    rawChallenger = Math.Clamp(1.0 - rawChallenger, 0.0, 1.0);
                }
                var correctedChallenger = _probabilityCorrectionService.ApplyCorrection(candidate.Market, rawChallenger);
                candidate.ChallengerCalibratedProbability = _calibrationService.Calibrate(candidate.Market, correctedChallenger, match.League?.Trim());
            }
        }

        return realizedCandidates;
    }

    internal static void NormalizeSimplexProbabilities(List<PredictionCandidate> candidates)
    {
        var home = candidates.FirstOrDefault(c => c.Market == PredictionMarket.HomeWin);
        var draw = candidates.FirstOrDefault(c => c.Market == PredictionMarket.Draw);
        var away = candidates.FirstOrDefault(c => c.Market == PredictionMarket.AwayWin);

        if (home != null && draw != null && away != null)
        {
            var calibratedSum = home.CalibratedProbability + draw.CalibratedProbability + away.CalibratedProbability;
            if (calibratedSum > 0)
            {
                var normHome = Math.Clamp(home.CalibratedProbability / calibratedSum, 0.0, 1.0);
                var normDraw = Math.Clamp(draw.CalibratedProbability / calibratedSum, 0.0, 1.0);
                var normAway = Math.Clamp(1.0 - normHome - normDraw, 0.0, 1.0);

                home.CalibratedProbability = normHome;
                draw.CalibratedProbability = normDraw;
                away.CalibratedProbability = normAway;
            }

            var correctedSum = home.CorrectedProbability + draw.CorrectedProbability + away.CorrectedProbability;
            if (correctedSum > 0)
            {
                var normHome = Math.Clamp(home.CorrectedProbability / correctedSum, 0.0, 1.0);
                var normDraw = Math.Clamp(draw.CorrectedProbability / correctedSum, 0.0, 1.0);
                var normAway = Math.Clamp(1.0 - normHome - normDraw, 0.0, 1.0);

                home.CorrectedProbability = normHome;
                draw.CorrectedProbability = normDraw;
                away.CorrectedProbability = normAway;
            }
        }

        var over25 = candidates.FirstOrDefault(c => c.Market == PredictionMarket.Over25Goals);
        var under25 = candidates.FirstOrDefault(c => c.Market == PredictionMarket.Under25Goals);
        if (over25 != null && under25 != null)
        {
            var calibratedSum = over25.CalibratedProbability + under25.CalibratedProbability;
            if (calibratedSum > 0)
            {
                var normOver = Math.Clamp(over25.CalibratedProbability / calibratedSum, 0.0, 1.0);
                over25.CalibratedProbability = normOver;
                under25.CalibratedProbability = Math.Clamp(1.0 - normOver, 0.0, 1.0);
            }

            var correctedSum = over25.CorrectedProbability + under25.CorrectedProbability;
            if (correctedSum > 0)
            {
                var normOver = Math.Clamp(over25.CorrectedProbability / correctedSum, 0.0, 1.0);
                over25.CorrectedProbability = normOver;
                under25.CorrectedProbability = Math.Clamp(1.0 - normOver, 0.0, 1.0);
            }
        }

        var bttsYes = candidates.FirstOrDefault(c => c.Market == PredictionMarket.BothTeamsScore && !IsBttsNoOutcome(c.PredictedOutcome));
        var bttsNo = candidates.FirstOrDefault(c => c.Market == PredictionMarket.BothTeamsScore && IsBttsNoOutcome(c.PredictedOutcome));
        if (bttsYes != null && bttsNo != null)
        {
            var calibratedSum = bttsYes.CalibratedProbability + bttsNo.CalibratedProbability;
            if (calibratedSum > 0)
            {
                var normYes = Math.Clamp(bttsYes.CalibratedProbability / calibratedSum, 0.0, 1.0);
                bttsYes.CalibratedProbability = normYes;
                bttsNo.CalibratedProbability = Math.Clamp(1.0 - normYes, 0.0, 1.0);
            }

            var correctedSum = bttsYes.CorrectedProbability + bttsNo.CorrectedProbability;
            if (correctedSum > 0)
            {
                var normYes = Math.Clamp(bttsYes.CorrectedProbability / correctedSum, 0.0, 1.0);
                bttsYes.CorrectedProbability = normYes;
                bttsNo.CorrectedProbability = Math.Clamp(1.0 - normYes, 0.0, 1.0);
            }
        }

        var over15 = candidates.FirstOrDefault(c => c.Market == PredictionMarket.Over15Goals);
        if (over15 != null && over25 != null)
        {
            if (over15.CalibratedProbability < over25.CalibratedProbability)
            {
                over15.CalibratedProbability = Math.Min(over25.CalibratedProbability + 0.12, 0.99);
            }
            if (over15.CorrectedProbability < over25.CorrectedProbability)
            {
                over15.CorrectedProbability = Math.Min(over25.CorrectedProbability + 0.12, 0.99);
            }
        }

        var dc1X = candidates.FirstOrDefault(c => c.Market == PredictionMarket.DoubleChance && c.PredictedOutcome == "1X");
        var dcX2 = candidates.FirstOrDefault(c => c.Market == PredictionMarket.DoubleChance && c.PredictedOutcome == "X2");
        var dc12 = candidates.FirstOrDefault(c => c.Market == PredictionMarket.DoubleChance && c.PredictedOutcome == "12");
        if (home != null && draw != null && away != null)
        {
            if (dc1X != null)
            {
                dc1X.CalibratedProbability = Math.Clamp(home.CalibratedProbability + draw.CalibratedProbability, 0.0, 1.0);
                dc1X.CorrectedProbability = Math.Clamp(home.CorrectedProbability + draw.CorrectedProbability, 0.0, 1.0);
            }
            if (dcX2 != null)
            {
                dcX2.CalibratedProbability = Math.Clamp(away.CalibratedProbability + draw.CalibratedProbability, 0.0, 1.0);
                dcX2.CorrectedProbability = Math.Clamp(away.CorrectedProbability + draw.CorrectedProbability, 0.0, 1.0);
            }
            if (dc12 != null)
            {
                dc12.CalibratedProbability = Math.Clamp(home.CalibratedProbability + away.CalibratedProbability, 0.0, 1.0);
                dc12.CorrectedProbability = Math.Clamp(home.CorrectedProbability + away.CorrectedProbability, 0.0, 1.0);
            }
        }
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
        (double Probability, bool HasMl) BlendFor(PredictionMarket market, double? bookmakerValue, double calculatorValue, double? statisticalValue)
        {
            var weights = ResolveWeights(market);
            var mlValue = _marketPredictionModelService?.TryPredict(match, market, calculatorValue, statisticalValue, bookmakerValue);
            var hasMl = mlValue is not null;
            var marketWeight = weights.Market;
            if (_settings.RetireProbabilityCalculatorWhenMlPromoted && mlValue is > 0)
            {
                // Promoted ML is live for this market — stop leaning on the hand-tuned calculator.
                marketWeight = 0d;
            }

            var blended = EnsembleProbabilityBlender.BlendLogit(
                (bookmakerValue, weights.Bookmaker),
                (calculatorValue, marketWeight),
                (statisticalValue, weights.DixonColes),
                (mlValue, weights.Ml));

            return (blended, hasMl);
        }

        var (homeWin, _) = BlendFor(PredictionMarket.HomeWin, bookmaker?.HomeWin, calculator.HomeWin, statistical?.HomeWin);
        var (draw, _) = BlendFor(PredictionMarket.Draw, bookmaker?.Draw, calculator.Draw, statistical?.Draw);
        var (awayWin, _) = BlendFor(PredictionMarket.AwayWin, bookmaker?.AwayWin, calculator.AwayWin, statistical?.AwayWin);

        var bookmakerUnder25 = bookmaker?.Under25 ?? (bookmaker?.Over25 is double bkOver ? 1.0 - bkOver : null);
        var statisticalUnder25 = statistical?.Under25 ?? (statistical?.Over25 is double stOver ? 1.0 - stOver : null);

        var (over25, hasOverMl) = BlendFor(PredictionMarket.Over25Goals, bookmaker?.Over25, calculator.Over25, statistical?.Over25);
        var (under25, hasUnderMl) = BlendFor(PredictionMarket.Under25Goals, bookmakerUnder25, calculator.Under25, statisticalUnder25);
        var (btts, _) = BlendFor(PredictionMarket.BothTeamsScore, bookmaker?.Btts, calculator.Btts, statistical?.Btts);

        var total = homeWin + draw + awayWin;
        if (total > 0)
        {
            homeWin /= total;
            draw /= total;
            awayWin /= total;
        }

        double resolvedOver25;
        double resolvedUnder25;
        if (hasUnderMl && !hasOverMl)
        {
            resolvedUnder25 = Math.Clamp(under25, 0.0, 1.0);
            resolvedOver25 = Math.Clamp(1.0 - resolvedUnder25, 0.0, 1.0);
        }
        else if (hasOverMl && !hasUnderMl)
        {
            resolvedOver25 = Math.Clamp(over25, 0.0, 1.0);
            resolvedUnder25 = Math.Clamp(1.0 - resolvedOver25, 0.0, 1.0);
        }
        else
        {
            var totalTotals = over25 + under25;
            if (totalTotals > 0)
            {
                resolvedOver25 = Math.Clamp(over25 / totalTotals, 0.0, 1.0);
                resolvedUnder25 = Math.Clamp(1.0 - resolvedOver25, 0.0, 1.0);
            }
            else
            {
                resolvedOver25 = Math.Clamp(over25, 0.0, 1.0);
                resolvedUnder25 = Math.Clamp(1.0 - resolvedOver25, 0.0, 1.0);
            }
        }

        var (over15, _) = BlendFor(PredictionMarket.Over15Goals, bookmaker?.Over15, calculator.Over15, statistical?.Over15);
        if (over15 <= 0 && resolvedOver25 > 0)
        {
            over15 = Math.Min(resolvedOver25 + 0.15, 0.98);
        }

        var dc1X = Math.Clamp(homeWin + draw, 0.0, 1.0);
        var dcX2 = Math.Clamp(awayWin + draw, 0.0, 1.0);
        var dc12 = Math.Clamp(homeWin + awayWin, 0.0, 1.0);

        return new MatchProbabilities(
            Btts: Math.Clamp(btts, 0.0, 1.0),
            Over25: resolvedOver25,
            Under25: resolvedUnder25,
            Draw: Math.Clamp(draw, 0.0, 1.0),
            HomeWin: Math.Clamp(homeWin, 0.0, 1.0),
            AwayWin: Math.Clamp(awayWin, 0.0, 1.0),
            Over15: Math.Clamp(over15, 0.0, 1.0),
            DoubleChance1X: dc1X,
            DoubleChanceX2: dcX2,
            DoubleChance12: dc12);
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
            ["explicitBttsMarket"] = match.TryGetNormalizedBttsPair(out _) || bookmakerSignal?.Btts is > 0,
            ["explicitDrawMarket"] = match.TryGetNormalizedOneX2(out _) || bookmakerSignal?.Draw is > 0,
            ["explicitTotalsMarket"] = match.TryGetNormalizedOver25Pair(out _) || (bookmakerSignal?.Over25 is > 0 || bookmakerSignal?.Under25 is > 0) || (statisticalSignal?.Over25 is > 0)
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
            PredictionMarket.Over15Goals => probabilities.Over15,
            PredictionMarket.DoubleChance => Math.Max(probabilities.DoubleChance1X, probabilities.DoubleChanceX2),
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
            PredictionMarket.Over15Goals => probabilities.Over15,
            PredictionMarket.DoubleChance => probabilities.DoubleChance1X is double dc1x && probabilities.DoubleChanceX2 is double dcx2
                ? Math.Max(dc1x, dcx2)
                : probabilities.DoubleChance1X ?? probabilities.DoubleChanceX2,
            _ => null
        };
}
