namespace MatchPredictor.Domain.Models;

public enum FixtureMatchRejectionReason
{
    None = 0,
    NoTeamMatch,
    QualifierMismatch,
    BelowFuzzyFloor,
    AmbiguousMargin,
    ReciprocalMismatch,
    DateMiss,
    NoCandidates
}
