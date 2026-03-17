namespace MatchPredictor.Domain.Models;

public class PredictionOddsSnapshot
{
    public int Id { get; set; }
    public int PredictionId { get; set; }
    public Guid PredictionRunId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public double DecimalOdds { get; set; }
    public double ImpliedProbability { get; set; }
    public string OddsDerivationSource { get; set; } = string.Empty;
    public PredictionOddsSnapshotKind SnapshotKind { get; set; }
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}
