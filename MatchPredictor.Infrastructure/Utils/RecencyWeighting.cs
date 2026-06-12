namespace MatchPredictor.Infrastructure.Utils;

public static class RecencyWeighting
{
    public static double CalculateWeight(DateTime timestampUtc, double halfLifeDays)
    {
        var ageDays = Math.Max((DateTime.UtcNow - timestampUtc).TotalDays, 0.0);
        return Math.Pow(0.5, ageDays / halfLifeDays);
    }
}
