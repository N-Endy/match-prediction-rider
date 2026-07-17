namespace MatchPredictor.Domain.Models;

public class ForecastObservation
{
    public int Id { get; set; }

    /// <summary>
    /// Deprecated legacy local date string. Retained and backfilled for backward compatibility
    /// only. Do not use in logic — read <see cref="MatchLocalDate"/> (or <see cref="MatchDateTime"/>)
    /// instead. Scheduled for removal in a future migration.
    /// </summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>
    /// Deprecated legacy local time string. Retained and backfilled for backward compatibility
    /// only. Do not use in logic — read <see cref="MatchLocalTime"/> (or <see cref="MatchDateTime"/>)
    /// instead. Scheduled for removal in a future migration.
    /// </summary>
    public string Time { get; set; } = string.Empty;

    /// <summary>Canonical local match date. Authoritative for all date logic.</summary>
    public DateOnly MatchLocalDate { get; set; }

    /// <summary>Canonical local kickoff time. Authoritative for all time logic.</summary>
    public TimeOnly? MatchLocalTime { get; set; }

    /// <summary>Canonical kickoff instant in UTC. Authoritative for all absolute-time logic.</summary>
    public DateTime? MatchDateTime { get; set; }
    public string FixtureKey { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public PredictionMarket Market { get; set; }
    public string PredictedOutcome { get; set; } = string.Empty;
    public double RawProbability { get; set; }
    public double CorrectedProbability { get; set; }
    public double CalibratedProbability { get; set; }
    public string CalibratorUsed { get; set; } = "Unknown";
    public double ThresholdUsed { get; set; }
    public string ThresholdSource { get; set; } = "Unknown";
    public string FeatureContributionsJson { get; set; } = "{}";
    public bool? OutcomeOccurred { get; set; }
    public string? ActualOutcome { get; set; }
    public string? ActualScore { get; set; }
    public string? SettledSourceName { get; set; }
    public string? SettledSourceEventId { get; set; }
    public bool IsPublished { get; set; }
    public bool IsLive { get; set; }
    public bool IsSettled { get; set; }
    public Guid PredictionRunId { get; set; }
    public string RunLabel { get; set; } = string.Empty;
    public string RunReason { get; set; } = string.Empty;
    public bool IsCurrentRevision { get; set; } = true;
    public int RevisionNumber { get; set; } = 1;
    public DateTime? SupersededAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAt { get; set; }
}
