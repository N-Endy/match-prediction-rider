namespace MatchPredictor.Domain.Models;

public enum BetslipRecordSection
{
    Rollover,
    Banker,
    Ladder,
    AiDraws
}

public enum BetslipSelectionHitStatus
{
    Pending,
    Won,
    Lost,
    Live
}

public sealed class BetslipRecordsForDate
{
    public DateOnly Date { get; init; }
    public BetslipRecordSection Section { get; init; }
    public IReadOnlyList<BetslipRecordRun> Runs { get; init; } = [];
    public IReadOnlyDictionary<int, Prediction> PredictionsById { get; init; } =
        new Dictionary<int, Prediction>();
    public IReadOnlyList<Prediction> FallbackPredictions { get; init; } = [];
}

public sealed class BetslipRecordRun
{
    public string RunLabel { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; }
    public bool IsCurrent { get; init; }
    public string DayKind { get; init; } = string.Empty;
    public IReadOnlyList<Betslip> Slips { get; init; } = [];
}
