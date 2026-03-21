using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Infrastructure.Services;

public class DataAnalyzerService : IDataAnalyzerService
{
    private readonly IProbabilityCalculator _probabilityCalculator;
    private readonly ICalibrationService _calibrationService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly PredictionSettings _settings;

    public DataAnalyzerService(
        IProbabilityCalculator probabilityCalculator,
        ICalibrationService calibrationService,
        IThresholdTuningService thresholdTuningService,
        IOptions<PredictionSettings> options)
    {
        _probabilityCalculator = probabilityCalculator;
        _calibrationService = calibrationService;
        _thresholdTuningService = thresholdTuningService;
        _settings = options.Value;
    }

    public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches)
    {
        return matches
            .SelectMany(BuildForecastCandidatesForMatch)
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates)
    {
        var forecasts = forecastCandidates.ToList();
        var thresholdDecisions = BuildThresholdDecisions();

        foreach (var candidate in forecasts)
        {
            if (thresholdDecisions.TryGetValue(candidate.Market, out var decision))
            {
                candidate.ThresholdUsed = decision.Threshold;
                candidate.ThresholdSource = decision.ThresholdSource;
            }
        }

        var published = new List<PredictionCandidate>();
        foreach (var fixtureGroup in forecasts.GroupBy(candidate =>
                     (candidate.MatchLocalDate, candidate.HomeTeam, candidate.AwayTeam, candidate.League)))
        {
            PublishBestInPair(fixtureGroup, published, thresholdDecisions, PredictionMarket.HomeWin, PredictionMarket.AwayWin);
            PublishBestInPair(fixtureGroup, published, thresholdDecisions, PredictionMarket.Over25Sets, PredictionMarket.Under25Sets);
            PublishBestInPair(fixtureGroup, published, thresholdDecisions, PredictionMarket.HomeSetHandicap, PredictionMarket.AwaySetHandicap);
        }

        return published;
    }

    public IReadOnlyList<PredictionCandidate> MatchWinner(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market is PredictionMarket.HomeWin or PredictionMarket.AwayWin)
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> OverUnderSets(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market is PredictionMarket.Over25Sets or PredictionMarket.Under25Sets)
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> SetHandicap(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market is PredictionMarket.HomeSetHandicap or PredictionMarket.AwaySetHandicap)
            .ToList();
    }

    private IReadOnlyDictionary<PredictionMarket, ThresholdDecision> BuildThresholdDecisions()
    {
        return new Dictionary<PredictionMarket, ThresholdDecision>
        {
            [PredictionMarket.HomeWin] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.HomeWin, _settings.HomeWinStrong),
            [PredictionMarket.AwayWin] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.AwayWin, _settings.AwayWinStrong),
            [PredictionMarket.Over25Sets] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.Over25Sets, _settings.OverTwoPointFiveSetsStrongThreshold),
            [PredictionMarket.Under25Sets] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.Under25Sets, _settings.UnderTwoPointFiveSetsStrongThreshold),
            [PredictionMarket.HomeSetHandicap] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.HomeSetHandicap, _settings.HomeSetHandicapStrongThreshold),
            [PredictionMarket.AwaySetHandicap] = _thresholdTuningService.GetThresholdDecision(PredictionMarket.AwaySetHandicap, _settings.AwaySetHandicapStrongThreshold)
        };
    }

    private static void PublishBestInPair(
        IGrouping<(DateOnly MatchLocalDate, string HomeTeam, string AwayTeam, string League), PredictionCandidate> fixtureGroup,
        ICollection<PredictionCandidate> published,
        IReadOnlyDictionary<PredictionMarket, ThresholdDecision> thresholdDecisions,
        PredictionMarket firstMarket,
        PredictionMarket secondMarket)
    {
        var bestCandidate = fixtureGroup
            .Where(candidate => candidate.Market == firstMarket || candidate.Market == secondMarket)
            .OrderByDescending(candidate => candidate.CalibratedProbability)
            .FirstOrDefault();

        if (bestCandidate is null)
        {
            return;
        }

        if (!thresholdDecisions.TryGetValue(bestCandidate.Market, out var decision) ||
            bestCandidate.CalibratedProbability < decision.Threshold)
        {
            return;
        }

        bestCandidate.WasPublished = true;
        published.Add(bestCandidate);
    }

    private IEnumerable<PredictionCandidate> BuildForecastCandidatesForMatch(MatchData match)
    {
        var probabilities = _probabilityCalculator.CalculateProbabilities(match);
        var candidates = new PredictionCandidate?[]
        {
            BuildCandidate(match, PredictionMarket.HomeWin, "Home Win", probabilities.HomeWin),
            BuildCandidate(match, PredictionMarket.AwayWin, "Away Win", probabilities.AwayWin),
            BuildCandidate(match, PredictionMarket.Over25Sets, "Over 2.5 Sets", probabilities.Over25Sets),
            BuildCandidate(match, PredictionMarket.Under25Sets, "Under 2.5 Sets", probabilities.Under25Sets),
            BuildCandidate(match, PredictionMarket.HomeSetHandicap, BuildSetHandicapOutcome(match, true), probabilities.HomeSetHandicap),
            BuildCandidate(match, PredictionMarket.AwaySetHandicap, BuildSetHandicapOutcome(match, false), probabilities.AwaySetHandicap)
        };

        return candidates.Where(candidate => candidate is not null).Cast<PredictionCandidate>();
    }

    private PredictionCandidate? BuildCandidate(MatchData match, PredictionMarket market, string predictedOutcome, double rawProbability)
    {
        if (string.IsNullOrWhiteSpace(match.HomeTeam) || string.IsNullOrWhiteSpace(match.AwayTeam) || rawProbability <= 0)
        {
            return null;
        }

        var calibration = _calibrationService.CalibrateWithDecision(market, rawProbability);
        var kickoff = ResolveCanonicalKickoff(match);

        return new PredictionCandidate
        {
            Market = market,
            Date = DateTimeProvider.FormatLocalDate(kickoff.localDate),
            Time = DateTimeProvider.FormatLocalTime(kickoff.localTime),
            MatchLocalDate = kickoff.localDate,
            MatchLocalTime = kickoff.localTime,
            MatchDateTime = kickoff.utcDateTime,
            FixtureKey = string.Empty,
            League = match.Tournament?.Trim() ?? match.League?.Trim() ?? string.Empty,
            HomeTeam = match.HomeTeam?.Trim() ?? string.Empty,
            AwayTeam = match.AwayTeam?.Trim() ?? string.Empty,
            PredictionCategory = market.ToCategory(),
            PredictedOutcome = predictedOutcome,
            RawProbability = Math.Clamp(rawProbability, 0.0, 1.0),
            CalibratedProbability = Math.Clamp(calibration.Probability, 0.0, 1.0),
            CalibratorUsed = calibration.CalibratorUsed
        };
    }

    private static (DateOnly localDate, TimeOnly localTime, DateTime utcDateTime) ResolveCanonicalKickoff(MatchData match)
    {
        if (match.MatchDateTime.HasValue)
        {
            var utc = match.MatchDateTime.Value;
            return (
                match.MatchLocalDate ?? DateTimeProvider.ConvertUtcToLocalDate(utc),
                match.MatchLocalTime ?? DateTimeProvider.ConvertUtcToLocalTime(utc),
                utc);
        }

        return DateTimeProvider.ParseCanonicalMatchDateTime(match.Date, match.Time);
    }

    private static string BuildSetHandicapOutcome(MatchData match, bool homeSide)
    {
        var line = Math.Abs(match.SetHandicapLine) > 0 ? match.SetHandicapLine : -1.5;
        var sideLine = homeSide ? line : -line;
        var side = homeSide ? "Home" : "Away";
        return $"{side} {sideLine:+0.0;-0.0} Sets";
    }
}
