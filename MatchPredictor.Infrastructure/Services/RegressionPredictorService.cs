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
        var scores = _db.MatchScores.ToList();
        if (scores.Count == 0)
            return [];

        // Separate home and away stats (Fix #9)
        var homePlayed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var homeGf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var homeGa = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var awayPlayed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var awayGf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var awayGa = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in scores)
        {
            if (!TryParseScore(s.Score, out var h, out var a))
                continue;

            // Home team stats (when playing at home)
            homeGf.TryGetValue(s.HomeTeam, out var hgf); homeGf[s.HomeTeam] = hgf + h;
            homeGa.TryGetValue(s.HomeTeam, out var hga); homeGa[s.HomeTeam] = hga + a;
            homePlayed.TryGetValue(s.HomeTeam, out var hp); homePlayed[s.HomeTeam] = hp + 1;

            // Away team stats (when playing away)
            awayGf.TryGetValue(s.AwayTeam, out var agf); awayGf[s.AwayTeam] = agf + a;
            awayGa.TryGetValue(s.AwayTeam, out var aga); awayGa[s.AwayTeam] = aga + h;
            awayPlayed.TryGetValue(s.AwayTeam, out var ap); awayPlayed[s.AwayTeam] = ap + 1;
        }

        var globalAvgGoals = scores
            .Where(s => TryParseScore(s.Score, out _, out _))
            .Select(s => (double)SumScore(s.Score))
            .DefaultIfEmpty(2.5)
            .Average();

        double GetAvg(Dictionary<string, double> dict, Dictionary<string, int> playedDict, string team, double fallback)
        {
            return dict.TryGetValue(team, out var val)
                ? val / Math.Max(playedDict.GetValueOrDefault(team, 1), 1)
                : fallback;
        }

        var predictions = new List<RegressionPrediction>();

        double GetHistoricalWeight(string category, params (string MetricName, double MetricValue)[] fallbacks)
        {
            if (accuracies == null || accuracies.Count == 0 || fallbacks == null || fallbacks.Length == 0) return 1.0;

            foreach (var (metricName, metricValue) in fallbacks)
            {
                if (metricValue <= 0) continue;

                var profile = accuracies.FirstOrDefault(a => 
                    a.Category == category && 
                    a.MetricName == metricName && 
                    metricValue >= a.MetricRangeStart && 
                    metricValue < a.MetricRangeEnd);

                if (profile != null && profile.TotalPredictions >= 5)
                {
                    var weight = 1.0 + (profile.AccuracyPercentage - 0.50);
                    return Math.Clamp(weight, 0.7, 1.3);
                }
            }

            return 1.0;
        }

        foreach (var m in upcomingMatches)
        {
            if (string.IsNullOrWhiteSpace(m.HomeTeam) || string.IsNullOrWhiteSpace(m.AwayTeam))
                continue;

            var home = m.HomeTeam.Trim();
            var away = m.AwayTeam.Trim();

            // Use venue-specific stats (Fix #9)
            var homeTeamGf = GetAvg(homeGf, homePlayed, home, globalAvgGoals / 2.0);
            var homeTeamGa = GetAvg(homeGa, homePlayed, home, globalAvgGoals / 2.0);
            var awayTeamGf = GetAvg(awayGf, awayPlayed, away, globalAvgGoals / 2.0);
            var awayTeamGa = GetAvg(awayGa, awayPlayed, away, globalAvgGoals / 2.0);

            // Expected goals using venue-aware blend
            var lambdaHome = 0.55 * homeTeamGf + 0.45 * awayTeamGa;
            var lambdaAway = 0.55 * awayTeamGf + 0.45 * homeTeamGa;

            lambdaHome = Math.Clamp(lambdaHome, 0.1, 3.5);
            lambdaAway = Math.Clamp(lambdaAway, 0.1, 3.5);

            var over25 = ProbabilityOverTotal(lambdaHome + lambdaAway, threshold: 2.5);
            var btts = ProbabilityBothTeamsScore(lambdaHome, lambdaAway);

            // Bivariate Poisson score matrix for W/D/L (Fix #7)
            var homeWinProb = CalculateScoreMatrixHomeWin(lambdaHome, lambdaAway);
            var awayWinProb = CalculateScoreMatrixAwayWin(lambdaHome, lambdaAway);
            var drawProb = CalculateScoreMatrixDraw(lambdaHome, lambdaAway);

            // --- Apply Self-Learning Weights with Geometric Mean (Fix #11) ---
            var over25Weight = GetHistoricalWeight("Over2.5Goals", 
                                   ("OverTwoGoals", m.OverTwoGoals),
                                   ("OverThreeGoals", m.OverThreeGoals),
                                   ("OverOnePointFive", m.OverOnePointFive));
            var over25Weight2 = GetHistoricalWeight("Over2.5Goals", 
                                   ("AhMinusHalfHome", m.AhMinusHalfHome),
                                   ("AhMinusOneHome", m.AhMinusOneHome),
                                   ("HomeWin", m.HomeWin));
            over25 = Math.Clamp(over25 * Math.Sqrt(over25Weight * over25Weight2), 0.0, 1.0);

            var bttsWeight = GetHistoricalWeight("BothTeamsScore", 
                                 ("AhMinusHalfHome", m.AhMinusHalfHome),
                                 ("AhMinusOneHome", m.AhMinusOneHome),
                                 ("HomeWin", m.HomeWin));
            var bttsWeight2 = GetHistoricalWeight("BothTeamsScore", 
                                 ("OverTwoGoals", m.OverTwoGoals),
                                 ("OverThreeGoals", m.OverThreeGoals),
                                 ("OverOnePointFive", m.OverOnePointFive));
            btts = Math.Clamp(btts * Math.Sqrt(bttsWeight * bttsWeight2), 0.0, 1.0);

            var hwWeight = GetHistoricalWeight("StraightWin", 
                ("AhMinusHalfHome", m.AhMinusHalfHome),
                ("AhMinusOneHome", m.AhMinusOneHome),
                ("HomeWin", m.HomeWin));
            homeWinProb = Math.Clamp(homeWinProb * hwWeight, 0.0, 1.0);

            var awWeight = GetHistoricalWeight("StraightWin", 
                ("AhMinusHalfAway", m.AhMinusHalfAway),
                ("AhMinusOneAway", m.AhMinusOneAway),
                ("AwayWin", m.AwayWin));
            awayWinProb = Math.Clamp(awayWinProb * awWeight, 0.0, 1.0);
                
            var drawWeight = GetHistoricalWeight("Draw", 
                ("AhZeroHome", m.AhZeroHome),
                ("AhZeroAway", m.AhZeroAway),
                ("Draw", m.Draw));
            drawProb = Math.Clamp(drawProb * drawWeight, 0.0, 1.0);

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
            
            if (drawProb >= 0.25)
            {
                predictions.Add(new RegressionPrediction
                {
                    HomeTeam = home,
                    AwayTeam = away,
                    League = m.League ?? string.Empty,
                    PredictionCategory = "Draw",
                    PredictedOutcome = "Draw",
                    // Use raw draw probability directly — no inflation (Fix #8)
                    ConfidenceScore = (decimal)Math.Round(drawProb, 3),
                    ExpectedHomeGoals = Math.Round(lambdaHome, 2),
                    ExpectedAwayGoals = Math.Round(lambdaAway, 2),
                    Date = date,
                    Time = time
                });
            }
        }

        return predictions;
    }

    // --- Score Matrix Helpers (Fix #7) ---

    /// <summary>
    /// Bivariate Poisson: P(Home > Away) from a 0-5 × 0-5 score grid.
    /// </summary>
    private static double CalculateScoreMatrixHomeWin(double lambdaHome, double lambdaAway)
    {
        double prob = 0;
        for (var h = 0; h <= 5; h++)
            for (var a = 0; a < h; a++)
                prob += PoissonPmf(lambdaHome, h) * PoissonPmf(lambdaAway, a);
        return prob;
    }

    /// <summary>
    /// Bivariate Poisson: P(Away > Home) from a 0-5 × 0-5 score grid.
    /// </summary>
    private static double CalculateScoreMatrixAwayWin(double lambdaHome, double lambdaAway)
    {
        double prob = 0;
        for (var a = 0; a <= 5; a++)
            for (var h = 0; h < a; h++)
                prob += PoissonPmf(lambdaHome, h) * PoissonPmf(lambdaAway, a);
        return prob;
    }

    /// <summary>
    /// Bivariate Poisson: P(Home == Away) from a 0-5 × 0-5 score grid.
    /// </summary>
    private static double CalculateScoreMatrixDraw(double lambdaHome, double lambdaAway)
    {
        double prob = 0;
        for (var k = 0; k <= 5; k++)
            prob += PoissonPmf(lambdaHome, k) * PoissonPmf(lambdaAway, k);
        return prob;
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
}
