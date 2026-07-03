namespace MatchPredictor.Domain.Helpers;

using MatchPredictor.Domain.Models;

/// <summary>
/// Validates the sports-ai.dev Excel prediction feed after extraction. Used for
/// observability (/ops/health, scrape logs) and to decide whether the pipeline
/// should degrade to bookmaker + statistical signals when the workbook is empty
/// or malformed.
/// </summary>
public static class ExcelFeedQualityValidator
{
    public const string HealthyStatus = "Healthy";
    public const string DegradedStatus = "Degraded";
    public const string FailedStatus = "Failed";

    public static ExcelFeedQualityReport Evaluate(IReadOnlyList<MatchData> rows, int expectedMinimumRows = 1)
    {
        if (rows.Count == 0)
        {
            return new ExcelFeedQualityReport(
                FailedStatus,
                RowCount: 0,
                InvalidProbabilityCount: 0,
                MissingOneX2Count: 0,
                Message: "Excel feed returned zero rows for the target date.");
        }

        var invalidProbabilityCount = 0;
        var missingOneX2Count = 0;

        foreach (var row in rows)
        {
            if (!row.TryGetNormalizedOneX2(out _))
            {
                missingOneX2Count++;
            }

            if (HasOutOfRangeProbability(row))
            {
                invalidProbabilityCount++;
            }
        }

        if (rows.Count < expectedMinimumRows)
        {
            return new ExcelFeedQualityReport(
                DegradedStatus,
                rows.Count,
                invalidProbabilityCount,
                missingOneX2Count,
                $"Excel feed row count ({rows.Count}) is below the expected minimum ({expectedMinimumRows}).");
        }

        if (invalidProbabilityCount > 0 || missingOneX2Count > rows.Count / 2)
        {
            return new ExcelFeedQualityReport(
                DegradedStatus,
                rows.Count,
                invalidProbabilityCount,
                missingOneX2Count,
                $"Excel feed quality issue: {invalidProbabilityCount} row(s) with out-of-range probabilities, {missingOneX2Count} missing 1X2.");
        }

        return new ExcelFeedQualityReport(
            HealthyStatus,
            rows.Count,
            invalidProbabilityCount,
            missingOneX2Count,
            $"Excel feed healthy ({rows.Count} rows).");
    }

    private static bool HasOutOfRangeProbability(MatchData row)
    {
        var values = new[]
        {
            row.HomeWin, row.Draw, row.AwayWin,
            row.OverTwoGoals, row.UnderTwoGoals,
            row.BttsYes, row.BttsNo
        };

        return values.Any(value => value < 0 || value > 1.001);
    }
}

public sealed record ExcelFeedQualityReport(
    string Status,
    int RowCount,
    int InvalidProbabilityCount,
    int MissingOneX2Count,
    string Message);
