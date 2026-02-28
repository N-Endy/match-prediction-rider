namespace MatchPredictor.Domain.Models;

public class ModelAccuracy
{
    public int Id { get; set; }
    
    // e.g., "BothTeamsScore", "StraightWin_Home", "StraightWin_Away"
    public string Category { get; set; } = string.Empty;
    
    // The specific MatchData attribute being analyzed (e.g., "AhMinusHalfHome", "OverTwoGoals")
    public string MetricName { get; set; } = string.Empty;
    
    // The start bracket of the metric (e.g., 0.50)
    public double MetricRangeStart { get; set; }
    
    // The end bracket of the metric (e.g., 0.60)
    public double MetricRangeEnd { get; set; }
    
    public int TotalPredictions { get; set; }
    
    public int CorrectPredictions { get; set; }
    
    public double AccuracyPercentage { get; set; }
    
    public DateTime LastUpdated { get; set; }
}
