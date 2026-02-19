using System;
using System.Collections.Generic;
using System.Linq;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Infrastructure.Services;

public class RegressionPredictorService : IRegressionPredictorService
{
    private readonly ApplicationDbContext _db;

    public RegressionPredictorService(ApplicationDbContext db)
    {
        _db = db;
    }

    public IEnumerable<RegressionPrediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches)
    {
        // Load historical scores
        var scores = _db.MatchScores.ToList();
        if (scores.Count == 0)
            return [];

        // We'll compute GF/GA by iterating over all scores and filling both home and away teams
        var played = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var gf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var ga = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in scores)
        {
            if (!TryParseScore(s.Score, out var h, out var a))
                continue;

            // Home team
            gf.TryGetValue(s.HomeTeam, out var hgf); gf[s.HomeTeam] = hgf + h;
            ga.TryGetValue(s.HomeTeam, out var hga); ga[s.HomeTeam] = hga + a;
            played.TryGetValue(s.HomeTeam, out var hp); played[s.HomeTeam] = hp + 1;

            // Away team
            gf.TryGetValue(s.AwayTeam, out var agf); gf[s.AwayTeam] = agf + a;
            ga.TryGetValue(s.AwayTeam, out var aga); ga[s.AwayTeam] = aga + h;
            played.TryGetValue(s.AwayTeam, out var ap); played[s.AwayTeam] = ap + 1;
        }

        // Averages with smoothing to avoid division by zero
        var globalAvgGoals = scores
            .Where(s => TryParseScore(s.Score, out _, out _))
            .Select(s => (double)SumScore(s.Score))
            .DefaultIfEmpty(2.5)
            .Average();

        double GetAvg(Dictionary<string, double> dict, string team, double fallback)
        {
            return dict.TryGetValue(team, out var val)
                ? val / Math.Max(played.GetValueOrDefault(team, 1), 1)
                : fallback;
        }

        var predictions = new List<RegressionPrediction>();

        foreach (var m in upcomingMatches)
        {
            if (string.IsNullOrWhiteSpace(m.HomeTeam) || string.IsNullOrWhiteSpace(m.AwayTeam))
                continue;

            var home = m.HomeTeam.Trim();
            var away = m.AwayTeam.Trim();

            var homeGF = GetAvg(gf, home, globalAvgGoals / 2.0);
            var homeGA = GetAvg(ga, home, globalAvgGoals / 2.0);
            var awayGF = GetAvg(gf, away, globalAvgGoals / 2.0);
            var awayGA = GetAvg(ga, away, globalAvgGoals / 2.0);

            // Expected goals using a linear blend
            var lambdaHome = 0.55 * homeGF + 0.45 * awayGA;
            var lambdaAway = 0.55 * awayGF + 0.45 * homeGA;

            // Guardrails
            lambdaHome = Math.Clamp(lambdaHome, 0.1, 3.5);
            lambdaAway = Math.Clamp(lambdaAway, 0.1, 3.5);

            var over25 = ProbabilityOverTotal(lambdaHome + lambdaAway, threshold: 2.5);
            var btts = ProbabilityBothTeamsScore(lambdaHome, lambdaAway);

            var diff = lambdaHome - lambdaAway;
            var homeWinProb = Sigmoid(diff);
            var awayWinProb = 1 - homeWinProb;

            var (date, time) = DateTimeProvider.ParseProperDateAndTime(m.Date, m.Time);

            if (over25 >= 0.5)
            {
                predictions.Add(new RegressionPrediction
                {
                    HomeTeam = home,
                    AwayTeam = away,
                    League = m.League ?? string.Empty,
                    PredictionCategory = "Over2.5Goals",
                    PredictedOutcome = "Over 2.5",
                    ConfidenceScore = (decimal)Math.Round(over25, 3),
                    ExpectedHomeGoals = Math.Round(lambdaHome, 2),
                    ExpectedAwayGoals = Math.Round(lambdaAway, 2),
                    Date = date,
                    Time = time
                });
            }

            if (btts >= 0.5)
            {
                predictions.Add(new RegressionPrediction
                {
                    HomeTeam = home,
                    AwayTeam = away,
                    League = m.League ?? string.Empty,
                    PredictionCategory = "BTTS",
                    PredictedOutcome = "BTTS",
                    ConfidenceScore = (decimal)Math.Round(btts, 3),
                    ExpectedHomeGoals = Math.Round(lambdaHome, 2),
                    ExpectedAwayGoals = Math.Round(lambdaAway, 2),
                    Date = date,
                    Time = time
                });
            }

            if (homeWinProb >= 0.55 || awayWinProb >= 0.55)
            {
                var homeFavored = homeWinProb >= awayWinProb;
                predictions.Add(new RegressionPrediction
                {
                    HomeTeam = home,
                    AwayTeam = away,
                    League = m.League ?? string.Empty,
                    PredictionCategory = "StraightWin",
                    PredictedOutcome = homeFavored ? "Home Win" : "Away Win",
                    ConfidenceScore = (decimal)Math.Round(Math.Max(homeWinProb, awayWinProb), 3),
                    ExpectedHomeGoals = Math.Round(lambdaHome, 2),
                    ExpectedAwayGoals = Math.Round(lambdaAway, 2),
                    Date = date,
                    Time = time
                });
            }
        }

        return predictions;
    }

    private static bool TryParseScore(string score, out int home, out int away)
    {
        home = 0; away = 0;
        if (string.IsNullOrWhiteSpace(score)) return false;
        var parts = score.Split(':');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out home) && int.TryParse(parts[1], out away);
    }

    private static int SumScore(string score)
    {
        return TryParseScore(score, out var h, out var a) ? h + a : 0;
    }

    private static double ProbabilityOverTotal(double lambdaTotal, double threshold)
    {
        var limit = (int)Math.Floor(threshold);
        double cdf = 0;
        for (int k = 0; k <= limit; k++)
            cdf += PoissonPmf(lambdaTotal, k);
        return 1 - cdf;
    }

    private static double ProbabilityBothTeamsScore(double lambdaHome, double lambdaAway)
    {
        var pH0 = PoissonPmf(lambdaHome, 0);
        var pA0 = PoissonPmf(lambdaAway, 0);
        return 1 - pH0 - pA0 + pH0 * pA0;
    }

    private static double PoissonPmf(double lambda, int k)
    {
        return Math.Exp(-lambda) * Math.Pow(lambda, k) / Factorial(k);
    }

    private static double Factorial(int n)
    {
        if (n <= 1) return 1.0;
        double f = 1.0;
        for (int i = 2; i <= n; i++) f *= i;
        return f;
    }

    private static double Sigmoid(double x)
    {
        return 1.0 / (1.0 + Math.Exp(-x));
    }
}
