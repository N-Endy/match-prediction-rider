namespace MatchPredictor.Domain.Models;

public class SourceMarketSelection
{
    public string Market { get; set; } = string.Empty;
    public string Prediction { get; set; } = string.Empty;
    public string MarketId { get; set; } = string.Empty;
    public string? Specifier { get; set; }
    public string OutcomeId { get; set; } = string.Empty;
    public string? Descriptor { get; set; }
    public double? Probability { get; set; }
    public double? DecimalOdds { get; set; }
}
