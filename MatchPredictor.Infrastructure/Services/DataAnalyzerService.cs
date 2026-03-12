using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Infrastructure.Services;

public class DataAnalyzerService : IDataAnalyzerService
{
    private readonly IProbabilityCalculator _probabilityCalculator;
    private readonly ICalibrationService _calibrationService;
    private readonly PredictionSettings _settings;

    public DataAnalyzerService(
        IProbabilityCalculator probabilityCalculator,
        ICalibrationService calibrationService,
        IOptions<PredictionSettings> options)
    {
        _probabilityCalculator = probabilityCalculator;
        _calibrationService = calibrationService;
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

        published.AddRange(forecasts.Where(candidate =>
            candidate.Market == PredictionMarket.BothTeamsScore &&
            candidate.CalibratedProbability >= _settings.BttsScoreThreshold));

        published.AddRange(forecasts.Where(candidate =>
            candidate.Market == PredictionMarket.Over25Goals &&
            candidate.CalibratedProbability >= _settings.OverTwoGoalsStrongThreshold));

        published.AddRange(forecasts.Where(candidate =>
            candidate.Market == PredictionMarket.Draw &&
            candidate.CalibratedProbability >= _settings.DrawStrongThreshold));

        foreach (var matchGroup in forecasts
                     .Where(candidate => candidate.Market is PredictionMarket.HomeWin or PredictionMarket.AwayWin)
                     .GroupBy(candidate => (
                         candidate.Date,
                         candidate.HomeTeam,
                         candidate.AwayTeam,
                         candidate.League)))
        {
            var bestSide = matchGroup
                .OrderByDescending(candidate => candidate.CalibratedProbability)
                .First();

            var threshold = bestSide.Market switch
            {
                PredictionMarket.HomeWin => _settings.HomeWinStrong,
                PredictionMarket.AwayWin => _settings.AwayWinStrong,
                _ => double.MaxValue
            };

            if (bestSide.CalibratedProbability >= threshold)
            {
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

    public IReadOnlyList<PredictionCandidate> Draw(IEnumerable<MatchData> matches)
    {
        return SelectPublishedPredictions(BuildForecastCandidates(matches))
            .Where(candidate => candidate.Market == PredictionMarket.Draw)
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

        var calibratedProbability = _calibrationService.Calibrate(market, rawProbability);
        return CreateCandidate(match, market, predictedOutcome, rawProbability, calibratedProbability);
    }

    private static PredictionCandidate CreateCandidate(
        MatchData match,
        PredictionMarket market,
        string predictedOutcome,
        double rawProbability,
        double calibratedProbability)
    {
        var date = match.Date?.Trim() ?? string.Empty;
        var time = match.Time?.Trim() ?? string.Empty;
        DateTime? utcDateTime = match.MatchDateTime;

        if (utcDateTime is null)
        {
            var normalizedDateTime = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);
            date = normalizedDateTime.date;
            time = normalizedDateTime.time;
            utcDateTime = normalizedDateTime.utcDateTime;
        }

        return new PredictionCandidate
        {
            Market = market,
            Date = date,
            Time = time,
            MatchDateTime = utcDateTime,
            League = match.League?.Trim() ?? string.Empty,
            HomeTeam = match.HomeTeam?.Trim() ?? string.Empty,
            AwayTeam = match.AwayTeam?.Trim() ?? string.Empty,
            PredictionCategory = market.ToCategory(),
            PredictedOutcome = predictedOutcome,
            RawProbability = Math.Clamp(rawProbability, 0.0, 1.0),
            CalibratedProbability = Math.Clamp(calibratedProbability, 0.0, 1.0)
        };
    }

    private static bool HasRequiredTeams(MatchData match)
    {
        return !string.IsNullOrWhiteSpace(match.HomeTeam) && !string.IsNullOrWhiteSpace(match.AwayTeam);
    }

    private IEnumerable<PredictionCandidate> BuildForecastCandidatesForMatch(MatchData match)
    {
        var candidates = new[]
        {
            BuildCandidate(
                match,
                PredictionMarket.BothTeamsScore,
                "BTTS",
                _probabilityCalculator.CalculateBttsProbability(match)),
            BuildCandidate(
                match,
                PredictionMarket.Over25Goals,
                "Over 2.5",
                _probabilityCalculator.CalculateOverTwoGoalsProbability(match)),
            BuildCandidate(
                match,
                PredictionMarket.Draw,
                "Draw",
                _probabilityCalculator.CalculateDrawProbability(match)),
            BuildCandidate(
                match,
                PredictionMarket.HomeWin,
                "Home Win",
                _probabilityCalculator.CalculateHomeWinProbability(match)),
            BuildCandidate(
                match,
                PredictionMarket.AwayWin,
                "Away Win",
                _probabilityCalculator.CalculateAwayWinProbability(match))
        };

        return candidates
            .Where(candidate => candidate != null)
            .Cast<PredictionCandidate>();
    }
}
