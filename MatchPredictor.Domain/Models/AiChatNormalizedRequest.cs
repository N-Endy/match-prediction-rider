namespace MatchPredictor.Domain.Models;

public class AiChatNormalizedRequest
{
    public string RawPrompt { get; set; } = string.Empty;
    public AiChatIntent Intent { get; set; } = AiChatIntent.RecommendPicks;
    public List<AiChatRequestedMarket> RequestedMarkets { get; set; } = [];
    public List<AiChatRequestedFilter> RequestedFilters { get; set; } = [];
    public int? RequestedTotalCount { get; set; }
    public string Scope { get; set; } = "today";
    public bool BookableOnly { get; set; }
    public bool WantsBooking { get; set; }
    public double? TargetCombinedOdds { get; set; }
    public string SafetyBias { get; set; } = string.Empty;
    public bool ValueBias { get; set; }
    public string ReferencedContextMode { get; set; } = string.Empty;
    public string ActionDirective { get; set; } = string.Empty;
    public List<string> EntityTerms { get; set; } = [];
    public List<string> InterpretationNotes { get; set; } = [];
    public bool NeedsSemanticFallback { get; set; }
    public bool FlexibleMix { get; set; }
    public bool UsedSemanticFallback { get; set; }
}
