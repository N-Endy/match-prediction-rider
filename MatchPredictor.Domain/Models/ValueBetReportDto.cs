namespace MatchPredictor.Domain.Models;

public class ValueBetReportDto
{
    public DateTime GeneratedAtLocal { get; set; }
    public int ConsideredCandidateCount { get; set; }
    public int IncludedCandidateCount { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<ValueBetExclusionStat> ExclusionBreakdown { get; set; } = [];
    public List<ValueBetDto> Bets { get; set; } = [];
}

public class ValueBetExclusionStat
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Count { get; set; }
}
