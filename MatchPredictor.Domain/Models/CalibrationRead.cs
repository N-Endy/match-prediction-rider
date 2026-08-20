namespace MatchPredictor.Domain.Models;

public enum CalibrationReadKind
{
    Strong,
    Caution,
    Avoid
}

public sealed record CalibrationRead(CalibrationReadKind Kind, string Detail)
{
    public string Label => Kind switch
    {
        CalibrationReadKind.Strong => "Well Calibrated",
        CalibrationReadKind.Caution => "Mixed Calibration",
        CalibrationReadKind.Avoid => "Poorly Calibrated",
        _ => Kind.ToString()
    };
}
