namespace MatchPredictor.Domain.Models;

public class ProbabilityCalculatorSettings
{
    public double Over25SourceWeight { get; set; } = 0.72;
    public double Over25PoissonWeight { get; set; } = 0.28;
    public double WinSourceWeight { get; set; } = 0.48;
    public double WinHandicapWeight { get; set; } = 0.22;
    public double WinPoissonWeight { get; set; } = 0.30;
    public double DrawSourceWeight { get; set; } = 0.62;
    public double DrawPoissonWeight { get; set; } = 0.38;

    /// <summary>
    /// Dixon-Coles low-score dependency adjustment parameter (tau).
    /// Typically negative (-0.08 to -0.15) to inflate 0-0 and 1-1 low-scoring draws.
    /// Default is -0.11. Set to 0.0 to disable (reverting to standard independent Poisson).
    /// </summary>
    public double DixonColesRho { get; set; } = -0.11;
    public double BttsDirectWeight { get; set; } = 0.58;
    public double BttsHeuristicWeight { get; set; } = 0.32;
    public double BttsGoalSupportWeight { get; set; } = 0.10;
    public double Over15XgWeight { get; set; } = 0.90;
    public double Over25XgWeight { get; set; } = 1.45;
    public double Over35XgWeight { get; set; } = 0.75;
}
