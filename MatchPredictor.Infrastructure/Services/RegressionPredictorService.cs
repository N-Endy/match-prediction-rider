using MatchPredictor.Domain.Helpers;
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

    public IEnumerable<RegressionPrediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches, List<ModelAccuracy> accuracies)
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
            if (!ScoreParser.TryParse(s.Score, out var h, out var a))
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
            .Where(s => ScoreParser.TryParse(s.Score, out _, out _))
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

        double GetWeight(string category, params (string MetricName, double MetricValue)[] fallbacks)
            => HistoricalWeightCalculator.GetHistoricalWeight(accuracies, category, fallbacks);

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

            // --- Apply Self-Learning Weights to Statistical Projections with Fallbacks ---
            var over25Weight = GetWeight("Over2.5Goals", 
                                   ("OverTwoGoals", m.OverTwoGoals),
                                   ("OverThreeGoals", m.OverThreeGoals),
                                   ("OverOnePointFive", m.OverOnePointFive)) +
                               GetWeight("Over2.5Goals", 
                                   ("AhMinusHalfHome", m.AhMinusHalfHome),
                                   ("AhMinusOneHome", m.AhMinusOneHome),
                                   ("HomeWin", m.HomeWin));
            over25 = Math.Clamp(over25 * (over25Weight / 2.0), 0.0, 1.0);

            var bttsWeight = GetWeight("BothTeamsScore", 
                                 ("AhMinusHalfHome", m.AhMinusHalfHome),
                                 ("AhMinusOneHome", m.AhMinusOneHome),
                                 ("HomeWin", m.HomeWin)) +
                             GetWeight("BothTeamsScore", 
                                 ("OverTwoGoals", m.OverTwoGoals),
                                 ("OverThreeGoals", m.OverThreeGoals),
                                 ("OverOnePointFive", m.OverOnePointFive));
            btts = Math.Clamp(btts * (bttsWeight / 2.0), 0.0, 1.0);

            var hwWeight = GetWeight("StraightWin", 
                ("AhMinusHalfHome", m.AhMinusHalfHome),
                ("AhMinusOneHome", m.AhMinusOneHome),
                ("HomeWin", m.HomeWin));
            homeWinProb = Math.Clamp(homeWinProb * hwWeight, 0.0, 1.0);

            var awWeight = GetWeight("StraightWin", 
                ("AhMinusHalfAway", m.AhMinusHalfAway),
                ("AhMinusOneAway", m.AhMinusOneAway),
                ("AwayWin", m.AwayWin));
            awayWinProb = Math.Clamp(awayWinProb * awWeight, 0.0, 1.0);
            
            var drawWeight = GetWeight("Draw", 
                ("AhZeroHome", m.AhZeroHome),
                ("AhZeroAway", m.AhZeroAway),
                ("Draw", m.Draw));
                
            // Approximate draw probability mathematically (e.g. using difference in expected goals)
            // A common baseline is roughly 25-30% for closely contested matches.
            // Using a basic heuristic that if the gap between lambda is small, draw is more likely.
            var rawDrawProb = Math.Exp(-Math.Pow(diff, 2) / 2.0) * 0.30;
            var drawProb = Math.Clamp(rawDrawProb * drawWeight, 0.0, 1.0);

            var (date, time, _) = DateTimeProvider.ParseProperDateAndTime(m.Date, m.Time);

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
            
            if (drawProb >= 0.35)
            {
                predictions.Add(new RegressionPrediction
                {
                    HomeTeam = home,
                    AwayTeam = away,
                    League = m.League ?? string.Empty,
                    PredictionCategory = "Draw",
                    PredictedOutcome = "Draw",
                    // Scale it so it displays nicely if needed, draw probabilities are generally artificially lower
                    ConfidenceScore = (decimal)Math.Round(Math.Min(drawProb * 1.5, 0.99), 3),
                    ExpectedHomeGoals = Math.Round(lambdaHome, 2),
                    ExpectedAwayGoals = Math.Round(lambdaAway, 2),
                    Date = date,
                    Time = time
                });
            }
        }

        return predictions;
    }

    private static int SumScore(string score)
    {
        return ScoreParser.TryParse(score, out var h, out var a) ? h + a : 0;
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
