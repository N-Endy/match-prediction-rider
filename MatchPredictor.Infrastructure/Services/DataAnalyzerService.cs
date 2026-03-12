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

    public IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches)
    {
        return matches
            .Select(match => BuildCandidate(
                match,
                PredictionMarket.BothTeamsScore,
                "BTTS",
                _probabilityCalculator.CalculateBttsProbability(match)))
            .Where(candidate => candidate != null && candidate.CalibratedProbability >= _settings.BttsScoreThreshold)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches)
    {
        return matches
            .Select(match => BuildCandidate(
                match,
                PredictionMarket.Over25Goals,
                "Over 2.5",
                _probabilityCalculator.CalculateOverTwoGoalsProbability(match)))
            .Where(candidate => candidate != null && candidate.CalibratedProbability >= _settings.OverTwoGoalsStrongThreshold)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> Draw(IEnumerable<MatchData> matches)
    {
        return matches
            .Select(match => BuildCandidate(
                match,
                PredictionMarket.Draw,
                "Draw",
                _probabilityCalculator.CalculateDrawProbability(match)))
            .Where(candidate => candidate != null && candidate.CalibratedProbability >= _settings.DrawStrongThreshold)
            .Cast<PredictionCandidate>()
            .ToList();
    }

    public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches)
    {
        var candidates = new List<PredictionCandidate>();

        foreach (var match in matches)
        {
            if (!HasRequiredTeams(match))
                continue;

            var rawHome = _probabilityCalculator.CalculateHomeWinProbability(match);
            var rawAway = _probabilityCalculator.CalculateAwayWinProbability(match);
            var calibratedHome = _calibrationService.Calibrate(PredictionMarket.StraightWin, rawHome);
            var calibratedAway = _calibrationService.Calibrate(PredictionMarket.StraightWin, rawAway);

            var homeFavored = calibratedHome >= calibratedAway;
            var calibratedProbability = homeFavored ? calibratedHome : calibratedAway;
            var rawProbability = homeFavored ? rawHome : rawAway;
            var threshold = homeFavored ? _settings.HomeWinStrong : _settings.AwayWinStrong;

            if (calibratedProbability < threshold)
                continue;

            candidates.Add(CreateCandidate(
                match,
                PredictionMarket.StraightWin,
                homeFavored ? "Home Win" : "Away Win",
                rawProbability,
                calibratedProbability));
        }

        return candidates;
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
        var (date, time, utcDateTime) = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);

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
}
