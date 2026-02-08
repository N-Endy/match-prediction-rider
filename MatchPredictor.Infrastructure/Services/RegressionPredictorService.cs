using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Improved Poisson regression-based predictor with:
/// - Home/Away performance separation
/// - Exponentially Weighted Moving Average (EWMA) for recent form
/// - Data-driven Poisson distribution modeling
/// - Enhanced BTTS and total goals probability calculations
/// </summary>
public class RegressionPredictorService : IRegressionPredictorService
{
    private readonly ApplicationDbContext _db;
    
    // Configuration: Recency weighting - recent games weighted higher
    private const double EwmaAlpha = 0.4; // Higher = more weight on recent games
    private const int RecentFormLookbackDays = 30;

    public RegressionPredictorService(ApplicationDbContext db)
    {
        _db = db;
    }

    public IEnumerable<Prediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches)
    {
        // Load historical match scores
        var scores = _db.MatchScores.OrderByDescending(s => s.MatchTime).ToList();
        if (scores.Count == 0)
            return [];

        var now = DateTime.UtcNow;
        var recentFormCutoff = now.AddDays(-RecentFormLookbackDays);

        // Compute team statistics separated by home/away
        var teamStats = ComputeTeamStatistics(scores, now, recentFormCutoff);

        var predictions = new List<Prediction>();

        foreach (var m in upcomingMatches)
        {
            if (string.IsNullOrWhiteSpace(m.HomeTeam) || string.IsNullOrWhiteSpace(m.AwayTeam))
                continue;

            var home = m.HomeTeam.Trim();
            var away = m.AwayTeam.Trim();

            // Detect sport from league name
            var sport = DetectSport(m.League);

            // Get team statistics with fallback to defaults
            var homeStats = teamStats.GetValueOrDefault(home) ?? new TeamVenueStats();
            var awayStats = teamStats.GetValueOrDefault(away) ?? new TeamVenueStats();

            // Calculate expected goals using home/away separated metrics
            var lambdaHome = CalculateExpectedGoals(
                homeAttackHome: homeStats.HomeAttackAvg,
                homeAttackAway: homeStats.AwayAttackAvg,
                awayDefenseHome: awayStats.HomeDefenseAvg,
                awayDefenseAway: awayStats.AwayDefenseAvg,
                isHome: true
            );

            var lambdaAway = CalculateExpectedGoals(
                homeAttackHome: awayStats.HomeAttackAvg,
                homeAttackAway: awayStats.AwayAttackAvg,
                awayDefenseHome: homeStats.HomeDefenseAvg,
                awayDefenseAway: homeStats.AwayDefenseAvg,
                isHome: false
            );

            // Apply sport-specific guardrails
            var (minGoals, maxGoals) = GetSportGuardrails(sport);
            lambdaHome = Math.Clamp(lambdaHome, minGoals, maxGoals);
            lambdaAway = Math.Clamp(lambdaAway, minGoals, maxGoals);

            // Calculate probabilities using improved Poisson model
            var over25Prob = ProbabilityOverTotal(lambdaHome + lambdaAway, 2.5);
            var bttsProb = ProbabilityBothTeamsScore(lambdaHome, lambdaAway);
            var (homeWinProb, awayWinProb) = ProbabilityMatch(lambdaHome, lambdaAway);

            if (!string.IsNullOrWhiteSpace(m.Date) && !string.IsNullOrWhiteSpace(m.Time))
            {
                var (date, time) = DateTimeProvider.ParseProperDateAndTime(m.Date, m.Time);

                // Only add predictions with reasonable confidence thresholds
                
                // Over 2.5 Goals prediction (threshold: 50%)
                if (over25Prob >= 0.50)
                {
                    predictions.Add(new Prediction
                    {
                        HomeTeam = home,
                        AwayTeam = away,
                        League = m.League ?? string.Empty,
                        PredictionCategory = "Over2.5Goals",
                        PredictedOutcome = "Over 2.5",
                        ConfidenceScore = (decimal)Math.Round(over25Prob, 3),
                        Date = date,
                        Time = time
                    });
                }

                // BTTS prediction (threshold: 50%)
                if (bttsProb >= 0.50)
                {
                    predictions.Add(new Prediction
                    {
                        HomeTeam = home,
                        AwayTeam = away,
                        League = m.League ?? string.Empty,
                        PredictionCategory = "BothTeamsScore",
                        PredictedOutcome = "BTTS",
                        ConfidenceScore = (decimal)Math.Round(bttsProb, 3),
                        Date = date,
                        Time = time
                    });
                }

                // Match outcome prediction (threshold: 55% confidence in winner)
                var maxWinProb = Math.Max(homeWinProb, awayWinProb);
                if (maxWinProb >= 0.55)
                {
                    predictions.Add(new Prediction
                    {
                        HomeTeam = home,
                        AwayTeam = away,
                        League = m.League ?? string.Empty,
                        PredictionCategory = "StraightWin",
                        PredictedOutcome = homeWinProb > awayWinProb ? "Home Win" : "Away Win",
                        ConfidenceScore = (decimal)Math.Round(maxWinProb, 3),
                        Date = date,
                        Time = time
                    });
                }

                // Draw prediction - calculate draw probability
                var drawProb = CalculateDrawProbability(lambdaHome, lambdaAway);
                if (drawProb >= 0.25) // Lower threshold for draws as they're less common
                {
                    predictions.Add(new Prediction
                    {
                        HomeTeam = home,
                        AwayTeam = away,
                        League = m.League ?? string.Empty,
                        PredictionCategory = "Draw",
                        PredictedOutcome = "Draw",
                        ConfidenceScore = (decimal)Math.Round(drawProb, 3),
                        Date = date,
                        Time = time
                    });
                }
            }
        }

        return predictions;
    }

    /// <summary>
    /// Detect sport from league name
    /// </summary>
    private static string DetectSport(string? league)
    {
        if (string.IsNullOrWhiteSpace(league))
            return "Football";

        var lower = league.ToLower();
        
        if (lower.Contains("handball"))
            return "Handball";
        if (lower.Contains("hockey") || lower.Contains("sm-liiga") || lower.Contains("khl"))
            return "Hockey";
        if (lower.Contains("volleyball") || lower.Contains("volley"))
            return "Volleyball";
        
        return "Football"; // Default to football
    }

    /// <summary>
    /// Get sport-specific guardrails for expected goals
    /// </summary>
    private static (double min, double max) GetSportGuardrails(string sport)
    {
        return sport switch
        {
            "Handball" => (0.5, 15.0),  // Handball: 7-30 goals per team typical
            "Hockey" => (0.2, 4.0),     // Hockey: 1-3 goals per team typical
            "Volleyball" => (20.0, 50.0), // Volleyball: 20-30 points per team
            _ => (0.15, 4.0)             // Football: 0-3 goals per team typical
        };
    }

    /// <summary>
    /// Compute home and away performance metrics for each team with EWMA weighting
    /// </summary>
    private Dictionary<string, TeamVenueStats> ComputeTeamStatistics(
        List<MatchScore> scores, DateTime now, DateTime recentCutoff)
    {
        var teamStats = new Dictionary<string, TeamVenueStats>(StringComparer.OrdinalIgnoreCase);

        foreach (var score in scores)
        {
            if (!TryParseScore(score.Score, out var homeGoals, out var awayGoals))
                continue;

            var isRecent = score.MatchTime >= recentCutoff;
            var daysAgo = (now - score.MatchTime).TotalDays;
            var recencyWeight = isRecent ? Math.Pow(1 - EwmaAlpha, daysAgo) : 0.1;

            // Home team statistics
            if (!teamStats.ContainsKey(score.HomeTeam))
                teamStats[score.HomeTeam] = new TeamVenueStats();

            var homeTeamStats = teamStats[score.HomeTeam];
            homeTeamStats.HomeMatches++;
            homeTeamStats.HomeGoalsFor += homeGoals * recencyWeight;
            homeTeamStats.HomeGoalsAgainst += awayGoals * recencyWeight;
            homeTeamStats.HomeMatchWeight += recencyWeight;

            // Away team statistics
            if (!teamStats.ContainsKey(score.AwayTeam))
                teamStats[score.AwayTeam] = new TeamVenueStats();

            var awayTeamStats = teamStats[score.AwayTeam];
            awayTeamStats.AwayMatches++;
            awayTeamStats.AwayGoalsFor += awayGoals * recencyWeight;
            awayTeamStats.AwayGoalsAgainst += homeGoals * recencyWeight;
            awayTeamStats.AwayMatchWeight += recencyWeight;
        }

        // Calculate averages
        foreach (var stats in teamStats.Values)
        {
            stats.HomeAttackAvg = stats.HomeMatches > 0 
                ? stats.HomeGoalsFor / Math.Max(stats.HomeMatchWeight, 0.5) 
                : 1.5;
            stats.HomeDefenseAvg = stats.HomeMatches > 0 
                ? stats.HomeGoalsAgainst / Math.Max(stats.HomeMatchWeight, 0.5) 
                : 1.2;
            
            stats.AwayAttackAvg = stats.AwayMatches > 0 
                ? stats.AwayGoalsFor / Math.Max(stats.AwayMatchWeight, 0.5) 
                : 1.0;
            stats.AwayDefenseAvg = stats.AwayMatches > 0 
                ? stats.AwayGoalsAgainst / Math.Max(stats.AwayMatchWeight, 0.5) 
                : 1.5;
        }

        return teamStats;
    }

    /// <summary>
    /// Calculate expected goals using weighted combination of attack and defense metrics
    /// </summary>
    private double CalculateExpectedGoals(
        double homeAttackHome, double homeAttackAway, 
        double awayDefenseHome, double awayDefenseAway,
        bool isHome)
    {
        var attack = isHome ? homeAttackHome : homeAttackAway;
        var defense = isHome ? awayDefenseHome : awayDefenseAway;

        // Weighted blend: 60% attack strength, 40% opponent defense
        var expectedGoals = (0.6 * attack) + (0.4 * defense);

        // Home advantage factor: ~15% boost
        if (isHome)
            expectedGoals *= 1.15;

        return expectedGoals;
    }

    /// <summary>
    /// Calculate probability of Over 2.5 goals using Poisson distribution
    /// </summary>
    private double ProbabilityOverTotal(double lambdaTotal, double threshold)
    {
        var limit = (int)Math.Floor(threshold);
        double cdf = 0;

        for (int k = 0; k <= limit; k++)
        {
            cdf += PoissonPmf(lambdaTotal, k);
        }

        return 1 - cdf;
    }

    /// <summary>
    /// Calculate BTTS probability assuming independence between goals
    /// </summary>
    private double ProbabilityBothTeamsScore(double lambdaHome, double lambdaAway)
    {
        var pH0 = PoissonPmf(lambdaHome, 0);
        var pA0 = PoissonPmf(lambdaAway, 0);
        
        return 1 - pH0 - pA0 + (pH0 * pA0);
    }

    /// <summary>
    /// Calculate match outcome probabilities (Win/Loss) using Poisson
    /// </summary>
    private (double homeWin, double awayWin) ProbabilityMatch(
        double lambdaHome, double lambdaAway)
    {
        double homeWinProb = 0;
        double awayWinProb = 0;

        // Sum over realistic goal ranges (0-6 goals per team)
        for (int h = 0; h <= 6; h++)
        {
            for (int a = 0; a <= 6; a++)
            {
                var prob = PoissonPmf(lambdaHome, h) * PoissonPmf(lambdaAway, a);

                if (h > a)
                    homeWinProb += prob;
                else if (h < a)
                    awayWinProb += prob;
            }
        }

        // Normalize to ensure sum = 1.0
        var total = homeWinProb + awayWinProb;
        if (total > 0)
        {
            homeWinProb /= total;
            awayWinProb /= total;
        }

        return (homeWinProb, awayWinProb);
    }

    /// <summary>
    /// Calculate draw probability using Poisson distribution
    /// </summary>
    private double CalculateDrawProbability(double lambdaHome, double lambdaAway)
    {
        double drawProb = 0;

        // Sum probability of all draw scenarios (0-0, 1-1, 2-2, 3-3, 4-4)
        for (int goals = 0; goals <= 4; goals++)
        {
            drawProb += PoissonPmf(lambdaHome, goals) * PoissonPmf(lambdaAway, goals);
        }

        return drawProb;
    }

    /// <summary>
    /// Poisson probability mass function: P(X = k) = (e^-λ * λ^k) / k!
    /// </summary>
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

    private static bool TryParseScore(string score, out int home, out int away)
    {
        home = 0; away = 0;
        if (string.IsNullOrWhiteSpace(score)) return false;
        var parts = score.Split(':');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out home) && int.TryParse(parts[1], out away);
    }

    private class TeamVenueStats
    {
        // Home statistics
        public int HomeMatches { get; set; }
        public double HomeGoalsFor { get; set; }
        public double HomeGoalsAgainst { get; set; }
        public double HomeMatchWeight { get; set; }
        public double HomeAttackAvg { get; set; }
        public double HomeDefenseAvg { get; set; }

        // Away statistics
        public int AwayMatches { get; set; }
        public double AwayGoalsFor { get; set; }
        public double AwayGoalsAgainst { get; set; }
        public double AwayMatchWeight { get; set; }
        public double AwayAttackAvg { get; set; }
        public double AwayDefenseAvg { get; set; }
    }
}

