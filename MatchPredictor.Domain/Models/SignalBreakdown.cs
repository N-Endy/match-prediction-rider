namespace MatchPredictor.Domain.Models;

public sealed class ModelSignalSnapshot
{
    public double? Feed { get; init; }
    public double? Bookmaker { get; init; }
    public double? Statistical { get; init; }
    public double? MachineLearning { get; init; }
    public double? Ensemble { get; init; }
}

public sealed class SignalAgreementSnapshot
{
    public double SignalSpreadPoints { get; init; }
    public bool AllSignalsAlign { get; init; }
    public bool ModelDivergesFromBookmaker { get; init; }
    public bool ThinHistory { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class SignalBreakdownSnapshot
{
    public ModelSignalSnapshot ModelSignals { get; init; } = new();
    public SignalAgreementSnapshot SignalAgreement { get; init; } = new();
}
