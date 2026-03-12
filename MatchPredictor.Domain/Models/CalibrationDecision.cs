namespace MatchPredictor.Domain.Models;

public class CalibrationDecision
{
    public double Probability { get; set; }
    public string CalibratorUsed { get; set; } = "Bucket";
}
