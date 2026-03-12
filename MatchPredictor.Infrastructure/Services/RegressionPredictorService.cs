using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

public class RegressionPredictorService : IRegressionPredictorService
{
    private const int ScoreMatrixMaxGoals = 10;
    private readonly ApplicationDbContext _db;

    public RegressionPredictorService(ApplicationDbContext db)
    {
        _db = db;
    }

    public IEnumerable<RegressionPrediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches)
    {
        var scores = _db.MatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive)
            .ToList();

        if (scores.Count == 0)
            return [];

        var homePlayed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var homeGf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var homeGa = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var awayPlayed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var awayGf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var awayGa = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var score in scores)
        {
            if (!TryParseScore(score.Score, out var homeGoals, out var awayGoals))
                continue;

            homeGf.TryGetValue(score.HomeTeam, out var homeGoalsFor);
            homeGf[score.HomeTeam] = homeGoalsFor + homeGoals;
            homeGa.TryGetValue(score.HomeTeam, out var homeGoalsAgainst);
            homeGa[score.HomeTeam] = homeGoalsAgainst + awayGoals;
            homePlayed.TryGetValue(score.HomeTeam, out var homeMatches);
            homePlayed[score.HomeTeam] = homeMatches + 1;

            awayGf.TryGetValue(score.AwayTeam, out var awayGoalsFor);
            awayGf[score.AwayTeam] = awayGoalsFor + awayGoals;
            awayGa.TryGetValue(score.AwayTeam, out var awayGoalsAgainst);
            awayGa[score.AwayTeam] = awayGoalsAgainst + homeGoals;
            awayPlayed.TryGetValue(score.AwayTeam, out var awayMatches);
            awayPlayed[score.AwayTeam] = awayMatches + 1;
        }

        var globalAvgGoals = scores
            .Where(score => TryParseScore(score.Score, out _, out _))
            .Select(score => (double)SumScore(score.Score))
            .DefaultIfEmpty(2.5)
            .Average();

        double GetAverage(Dictionary<string, double> goals, Dictionary<string, int> matchesPlayed, string team, double fallback)
        {
            return goals.TryGetValue(team, out var totalGoals)
                ? totalGoals / Math.Max(matchesPlayed.GetValueOrDefault(team, 1), 1)
                : fallback;
        }

        var predictions = new List<RegressionPrediction>();

        foreach (var match in upcomingMatches)
        {
            if (string.IsNullOrWhiteSpace(match.HomeTeam) || string.IsNullOrWhiteSpace(match.AwayTeam))
                continue;

            var homeTeam = match.HomeTeam.Trim();
            var awayTeam = match.AwayTeam.Trim();

            var homeTeamGf = GetAverage(homeGf, homePlayed, homeTeam, globalAvgGoals / 2.0);
            var homeTeamGa = GetAverage(homeGa, homePlayed, homeTeam, globalAvgGoals / 2.0);
            var awayTeamGf = GetAverage(awayGf, awayPlayed, awayTeam, globalAvgGoals / 2.0);
            var awayTeamGa = GetAverage(awayGa, awayPlayed, awayTeam, globalAvgGoals / 2.0);

            var lambdaHome = Math.Clamp((0.55 * homeTeamGf) + (0.45 * awayTeamGa), 0.1, 3.5);
            var lambdaAway = Math.Clamp((0.55 * awayTeamGf) + (0.45 * homeTeamGa), 0.1, 3.5);

            var over25 = ProbabilityOverTotal(lambdaHome + lambdaAway, threshold: 2.5);
            var btts = ProbabilityBothTeamsScore(lambdaHome, lambdaAway);
            var (homeWinProb, awayWinProb, drawProb) = CalculateNormalizedWdl(lambdaHome, lambdaAway);

            var (date, time, _) = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);

            if (over25 >= 0.5)
            {
                predictions.Add(CreatePrediction(
                    homeTeam,
                    awayTeam,
                    match.League,
                    "Over2.5Goals",
                    "Over 2.5",
                    over25,
                    lambdaHome,
                    lambdaAway,
                    date,
                    time));
            }

            if (btts >= 0.5)
            {
                predictions.Add(CreatePrediction(
                    homeTeam,
                    awayTeam,
                    match.League,
                    "BTTS",
                    "BTTS",
                    btts,
                    lambdaHome,
                    lambdaAway,
                    date,
                    time));
            }

            if (homeWinProb >= 0.55 || awayWinProb >= 0.55)
            {
                var homeFavored = homeWinProb >= awayWinProb;
                predictions.Add(CreatePrediction(
                    homeTeam,
                    awayTeam,
                    match.League,
                    "StraightWin",
                    homeFavored ? "Home Win" : "Away Win",
                    Math.Max(homeWinProb, awayWinProb),
                    lambdaHome,
                    lambdaAway,
                    date,
                    time));
            }

            if (drawProb >= 0.25)
            {
                predictions.Add(CreatePrediction(
                    homeTeam,
                    awayTeam,
                    match.League,
                    "Draw",
                    "Draw",
                    drawProb,
                    lambdaHome,
                    lambdaAway,
                    date,
                    time));
            }
        }

        return predictions;
    }

    private static RegressionPrediction CreatePrediction(
        string homeTeam,
        string awayTeam,
        string? league,
        string category,
        string outcome,
        double confidence,
        double expectedHomeGoals,
        double expectedAwayGoals,
        string date,
        string time)
    {
        return new RegressionPrediction
        {
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            League = league ?? string.Empty,
            PredictionCategory = category,
            PredictedOutcome = outcome,
            ConfidenceScore = (decimal)Math.Round(confidence, 3),
            ExpectedHomeGoals = Math.Round(expectedHomeGoals, 2),
            ExpectedAwayGoals = Math.Round(expectedAwayGoals, 2),
            Date = date,
            Time = time
        };
    }

    private static (double homeWin, double awayWin, double draw) CalculateNormalizedWdl(double lambdaHome, double lambdaAway)
    {
        var homeWin = 0.0;
        var awayWin = 0.0;
        var draw = 0.0;
        var includedMass = 0.0;

        for (var homeGoals = 0; homeGoals <= ScoreMatrixMaxGoals; homeGoals++)
        {
            for (var awayGoals = 0; awayGoals <= ScoreMatrixMaxGoals; awayGoals++)
            {
                var probability = PoissonPmf(lambdaHome, homeGoals) * PoissonPmf(lambdaAway, awayGoals);
                includedMass += probability;

                if (homeGoals > awayGoals)
                    homeWin += probability;
                else if (awayGoals > homeGoals)
                    awayWin += probability;
                else
                    draw += probability;
            }
        }

        if (includedMass <= 0)
            return (0.0, 0.0, 0.0);

        return (homeWin / includedMass, awayWin / includedMass, draw / includedMass);
    }

    private static bool TryParseScore(string score, out int home, out int away)
    {
        home = 0;
        away = 0;

        if (string.IsNullOrWhiteSpace(score))
            return false;

        var parts = score.Split(':');
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out home) && int.TryParse(parts[1], out away);
    }

    private static int SumScore(string score)
    {
        return TryParseScore(score, out var home, out var away) ? home + away : 0;
    }

    private static double ProbabilityOverTotal(double lambdaTotal, double threshold)
    {
        var limit = (int)Math.Floor(threshold);
        var cdf = 0.0;
        for (var goals = 0; goals <= limit; goals++)
            cdf += PoissonPmf(lambdaTotal, goals);

        return 1.0 - cdf;
    }

    private static double ProbabilityBothTeamsScore(double lambdaHome, double lambdaAway)
    {
        var homeFail = PoissonPmf(lambdaHome, 0);
        var awayFail = PoissonPmf(lambdaAway, 0);
        return 1.0 - homeFail - awayFail + (homeFail * awayFail);
    }

    private static double PoissonPmf(double lambda, int k)
    {
        return Math.Exp(-lambda) * Math.Pow(lambda, k) / Factorial(k);
    }

    private static double Factorial(int n)
    {
        if (n <= 1)
            return 1.0;

        var factorial = 1.0;
        for (var i = 2; i <= n; i++)
            factorial *= i;

        return factorial;
    }
}
