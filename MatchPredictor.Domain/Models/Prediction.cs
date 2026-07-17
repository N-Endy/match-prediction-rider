
namespace MatchPredictor.Domain.Models;

public class Prediction
{
    public int Id { get; set; }

    /// <summary>
    /// Deprecated legacy local date string (e.g. "dd-MM-yyyy"). Retained and backfilled for
    /// backward compatibility only. Do not use in logic — read <see cref="MatchLocalDate"/>
    /// (or <see cref="MatchDateTime"/>) instead. Scheduled for removal in a future migration.
    /// </summary>
    public string Date { get; set; } = null!;

    /// <summary>
    /// Deprecated legacy local time string (e.g. "HH:mm"). Retained and backfilled for backward
    /// compatibility only. Do not use in logic — read <see cref="MatchLocalTime"/>
    /// (or <see cref="MatchDateTime"/>) instead. Scheduled for removal in a future migration.
    /// </summary>
    public string Time { get; set; } = null!;

    /// <summary>Canonical local match date. Authoritative for all date logic.</summary>
    public DateOnly MatchLocalDate { get; set; }

    /// <summary>Canonical local kickoff time. Authoritative for all time logic.</summary>
    public TimeOnly? MatchLocalTime { get; set; }

    /// <summary>Canonical kickoff instant in UTC. Authoritative for all absolute-time logic.</summary>
    public DateTime? MatchDateTime { get; set; }
    public string FixtureKey { get; set; } = string.Empty;
    public string League { get; set; } = null!;
    public string HomeTeam { get; set; } = null!;
    public string AwayTeam { get; set; } = null!;
    public string PredictionCategory { get; set; } = null!;
    public string PredictedOutcome { get; set; } = null!;
    public decimal? RawConfidenceScore { get; set; }
    public decimal? ConfidenceScore { get; set; }
    public string CalibratorUsed { get; set; } = "Unknown";
    public double ThresholdUsed { get; set; }
    public string ThresholdSource { get; set; } = "Unknown";
    public bool WasPublished { get; set; } = true;
    public string? ActualOutcome { get; set; }
    public string? ActualScore { get; set; }
    public string? SettledSourceName { get; set; }
    public string? SettledSourceEventId { get; set; }
    public bool IsLive { get; set; }
    public Guid PredictionRunId { get; set; }
    public string RunLabel { get; set; } = string.Empty;
    public string RunReason { get; set; } = string.Empty;
    public bool IsCurrentRevision { get; set; } = true;
    public int RevisionNumber { get; set; } = 1;
    public DateTime? SupersededAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
