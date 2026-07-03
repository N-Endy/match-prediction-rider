using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class ExcelFeedQualityValidatorTests
{
    [Fact]
    public void Evaluate_ReturnsFailed_WhenNoRows()
    {
        var report = ExcelFeedQualityValidator.Evaluate([]);

        Assert.Equal(ExcelFeedQualityValidator.FailedStatus, report.Status);
        Assert.Equal(0, report.RowCount);
    }

    [Fact]
    public void Evaluate_ReturnsHealthy_WhenRowsHaveValidOneX2()
    {
        var rows = new List<MatchData>
        {
            new()
            {
                HomeWin = 0.45,
                Draw = 0.28,
                AwayWin = 0.27,
                OverTwoGoals = 0.52,
                UnderTwoGoals = 0.48
            }
        };

        var report = ExcelFeedQualityValidator.Evaluate(rows);

        Assert.Equal(ExcelFeedQualityValidator.HealthyStatus, report.Status);
        Assert.Equal(1, report.RowCount);
    }

    [Fact]
    public void Evaluate_ReturnsDegraded_WhenMostRowsMissingOneX2()
    {
        var rows = new List<MatchData>
        {
            new() { HomeWin = 0, Draw = 0, AwayWin = 0 },
            new() { HomeWin = 0, Draw = 0, AwayWin = 0 },
            new() { HomeWin = 0.4, Draw = 0.3, AwayWin = 0.3 }
        };

        var report = ExcelFeedQualityValidator.Evaluate(rows);

        Assert.Equal(ExcelFeedQualityValidator.DegradedStatus, report.Status);
    }
}
