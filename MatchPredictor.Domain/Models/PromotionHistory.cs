namespace MatchPredictor.Domain.Models;

public class PromotionHistory
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string PreviousValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public double? PreviousNumericValue { get; set; }
    public double? NewNumericValue { get; set; }
    public double? BaselineScore { get; set; }
    public double? CandidateScore { get; set; }
    public double? Improvement { get; set; }
    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;
}
