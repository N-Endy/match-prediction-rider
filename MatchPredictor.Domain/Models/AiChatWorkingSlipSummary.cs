namespace MatchPredictor.Domain.Models;

public class AiChatWorkingSlipSummary
{
    public int Count { get; set; }
    public int BookableCount { get; set; }
    public List<string> Markets { get; set; } = [];
    public double? EstimatedCombinedOdds { get; set; }
}
