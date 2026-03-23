namespace MatchPredictor.Domain.Models;

public class ValueBetReportDto
{
    public DateTime GeneratedAtLocal { get; set; }
    public int ConsideredCandidateCount { get; set; }
    public int IncludedCandidateCount { get; set; }
    public ValueBetPerformanceSummary PerformanceSummary { get; set; } = new();
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

public class ValueBetPerformanceSummary
{
    public int SettledBetCount { get; set; }
    public int WinningBetCount { get; set; }
    public double WinRate { get; set; }
    public double TotalStakedUnits { get; set; }
    public double NetProfitUnits { get; set; }
    public double RoiPercent { get; set; }
    public double YieldPercent { get; set; }
    public double MaxDrawdownUnits { get; set; }
    public int ClosingLineSamples { get; set; }
    public double AverageClosingLineValuePercent { get; set; }
}
