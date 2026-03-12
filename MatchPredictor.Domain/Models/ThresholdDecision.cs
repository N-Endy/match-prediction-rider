namespace MatchPredictor.Domain.Models;

public class ThresholdDecision
{
    public double Threshold { get; set; }
    public string ThresholdSource { get; set; } = "Configured";
}
