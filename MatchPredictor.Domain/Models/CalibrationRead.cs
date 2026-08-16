namespace MatchPredictor.Domain.Models;

public enum CalibrationReadKind
{
    Strong,
    Caution,
    Avoid
}

public sealed record CalibrationRead(CalibrationReadKind Kind, string Detail)
{
    public string Label => Kind.ToString();
}
