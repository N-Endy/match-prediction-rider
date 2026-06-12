using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
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
    private readonly PredictionSettings _settings;

    public DataAnalyzerService(
        IProbabilityCalculator probabilityCalculator,
        ICalibrationService calibrationService,
        IThresholdTuningService thresholdTuningService,
        IProbabilityCorrectionService probabilityCorrectionService,
        IOptions<PredictionSettings> options)
    {
        _probabilityCalculator = probabilityCalculator;
        _calibrationService = calibrationService;
        _thresholdTuningService = thresholdTuningService;
        _probabilityCorrectionService = probabilityCorrectionService;
        _settings = options.Value;
    }

    public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches)
    {
        return matches
            .SelectMany(BuildForecastCandidatesForMatch)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates)
    {
        var forecasts = forecastCandidates.ToList();
        var published = new List<PredictionCandidate>();
        var thresholdDecisions = new Dictionary<PredictionMarket, ThresholdDecision>
        {
            [PredictionMarket.BothTeamsScore] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.BothTeamsScore, _settings.BttsScoreThreshold),
            [PredictionMarket.Over25Goals] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.Over25Goals, _settings.OverTwoGoalsStrongThreshold),
            [PredictionMarket.Under25Goals] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.Under25Goals, _settings.UnderTwoGoalsStrongThreshold),
            [PredictionMarket.HomeWin] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.HomeWin, _settings.HomeWinStrong),
            [PredictionMarket.AwayWin] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.AwayWin, _settings.AwayWinStrong)
        };

        foreach (var candidate in forecasts)
        {
            if (thresholdDecisions.TryGetValue(candidate.Market, out var decision))
            {
                candidate.ThresholdUsed = decision.Threshold;
                candidate.ThresholdSource = decision.ThresholdSource;
            }
        }

        published.AddRange(MarkPublished(forecasts.Where(candidate =>
            candidate.Market == PredictionMarket.BothTeamsScore &&
            candidate.CalibratedProbability >= thresholdDecisions[PredictionMarket.BothTeamsScore].Threshold)));

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
                    thresholdDecisions.TryGetValue(candidate.Market, out var decision) &&
                    candidate.CalibratedProbability >= decision.Threshold)
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

            var threshold = bestSide.Market switch
            {
                PredictionMarket.HomeWin => thresholdDecisions[PredictionMarket.HomeWin].Threshold,
                PredictionMarket.AwayWin => thresholdDecisions[PredictionMarket.AwayWin].Threshold,
                _ => double.MaxValue
            };

            if (bestSide.CalibratedProbability >= threshold)
            {
                bestSide.WasPublished = true;
                published.Add(bestSide);
            }
        }

        return published;
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
        var calibration = _calibrationService.CalibrateWithDecision(market, correctedProbability);
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
        var date = match.Date?.Trim() ?? string.Empty;
        var time = match.Time?.Trim() ?? string.Empty;
        DateTime? utcDateTime = match.MatchDateTime;
        var matchLocalDate = match.MatchLocalDate;
        var matchLocalTime = match.MatchLocalTime;

        if (utcDateTime is null)
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

    private IEnumerable<PredictionCandidate> BuildForecastCandidatesForMatch(MatchData match)
    {
        var probabilities = _probabilityCalculator.CalculateProbabilities(match);
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
                probabilities.AwayWin)
        };

        var realizedCandidates = candidates
            .Where(candidate => candidate != null)
            .Cast<PredictionCandidate>()
            .ToList();

        foreach (var candidate in realizedCandidates)
        {
            candidate.FeatureContributionsJson = BuildFeatureContributionSummary(match, probabilities, candidate.Market);
        }

        return realizedCandidates;
    }

    private static string BuildFeatureContributionSummary(MatchData match, MatchProbabilities probabilities, PredictionMarket market)
    {
        var oneX2Available = match.TryGetNormalizedOneX2(out var oneX2);
        var over25Available = match.TryGetNormalizedOver25Pair(out var over25Pair);
        var bttsAvailable = match.TryGetNormalizedBttsPair(out var bttsPair);

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
            }
        };

        return JsonSerializer.Serialize(summary);
    }
}
