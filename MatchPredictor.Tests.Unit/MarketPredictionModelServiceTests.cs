using System.Text.Json;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class MarketPredictionModelServiceTests
{
    [Fact]
    public void ParseSignals_ReadsExplicitUnder25()
    {
        const string json = """
            {
              "market": "Under25Goals",
              "calculatorSignal": { "over25": 0.58, "under25": 0.42 },
              "statisticalSignal": { "over25": 0.61, "under25": 0.39 },
              "bookmakerSignal": { "over25": 0.55, "under25": 0.45 }
            }
            """;

        var signals = MarketPredictionModelService.ParseSignals(json, fallback: 0.5);

        Assert.Equal(0.42, signals.Calculator, 6);
        Assert.Equal(0.39, signals.Statistical.GetValueOrDefault(), 6);
        Assert.Equal(0.45, signals.Bookmaker.GetValueOrDefault(), 6);
    }

    [Fact]
    public void ParseSignals_ComplementsHistoricalOver25ForUnder25Market()
    {
        const string json = """
            {
              "market": "Under25Goals",
              "calculatorSignal": { "btts": 0.50, "over25": 0.62, "homeWin": 0.40, "awayWin": 0.30, "draw": 0.30 },
              "statisticalSignal": { "over25": 0.70 },
              "bookmakerSignal": { "over25": 0.55 }
            }
            """;

        var signals = MarketPredictionModelService.ParseSignals(json, fallback: 0.5);

        Assert.Equal(0.38, signals.Calculator, 6);
        Assert.Equal(0.30, signals.Statistical.GetValueOrDefault(), 6);
        Assert.Equal(0.45, signals.Bookmaker.GetValueOrDefault(), 6);
    }

    [Fact]
    public void MapFeatures_UsesNaNAndFlagsWhenSnapshotAndSignalsAreMissing()
    {
        var input = MarketPredictionModelService.MapFeatures(
            calculatorProbability: 0.58,
            statisticalProbability: null,
            bookmakerProbability: null,
            featureSnapshot: null);

        Assert.Equal(0.58f, input.CalculatorProbability);
        Assert.True(float.IsNaN(input.StatisticalProbability));
        Assert.True(float.IsNaN(input.BookmakerProbability));
        Assert.True(float.IsNaN(input.HomeRestDays));
        Assert.True(float.IsNaN(input.HomeFormPoints));
        Assert.True(float.IsNaN(input.HeadToHeadHomeWins));
        Assert.Equal(0f, input.HasSnapshot);
        Assert.Equal(0f, input.HasStatistical);
        Assert.Equal(0f, input.HasBookmaker);
        Assert.Equal(0f, input.HasRestDays);
    }

    [Fact]
    public void MapFeatures_KeepsSnapshotValuesAndMarksRestWhenPresent()
    {
        var snapshot = new FixtureFeatureSnapshot
        {
            FixtureKey = "fx-1",
            HomeRestDays = 6,
            AwayRestDays = 3,
            HomeFormPointsPerMatch = 1.4,
            AwayFormPointsPerMatch = 0.8,
            HeadToHeadHomeWins = 2,
            HeadToHeadDraws = 1,
            HeadToHeadAwayWins = 0
        };

        var input = MarketPredictionModelService.MapFeatures(
            calculatorProbability: 0.51,
            statisticalProbability: 0.47,
            bookmakerProbability: 0.49,
            featureSnapshot: snapshot);

        Assert.Equal(6f, input.HomeRestDays);
        Assert.Equal(3f, input.AwayRestDays);
        Assert.Equal(3f, input.RestDayDifferential);
        Assert.Equal(1.4f, input.HomeFormPoints);
        Assert.Equal(2f, input.HeadToHeadHomeWins);
        Assert.Equal(0f, input.HeadToHeadAwayWins);
        Assert.Equal(1f, input.HasSnapshot);
        Assert.Equal(1f, input.HasStatistical);
        Assert.Equal(1f, input.HasBookmaker);
        Assert.Equal(1f, input.HasRestDays);
    }

    [Fact]
    public void BuildTrainingRow_JoinsSnapshotAndDoesNotCopyCalculatorIntoMissingSignals()
    {
        var forecast = new ForecastObservation
        {
            FixtureKey = "fx-kiev",
            Market = PredictionMarket.Under25Goals,
            RawProbability = 0.58,
            OutcomeOccurred = true,
            CreatedAt = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc),
            MatchDateTime = new DateTime(2026, 8, 11, 15, 0, 0, DateTimeKind.Utc),
            FeatureContributionsJson = JsonSerializer.Serialize(new
            {
                market = "Under25Goals",
                calculatorSignal = new { over25 = 0.60 },
                statisticalSignal = (object?)null,
                bookmakerSignal = (object?)null
            })
        };
        var snapshots = new[]
        {
            new FixtureFeatureSnapshot
            {
                FixtureKey = "fx-kiev",
                CapturedAtUtc = new DateTime(2026, 8, 11, 10, 0, 0, DateTimeKind.Utc),
                HomeRestDays = 4
            }
        };

        var row = MarketPredictionModelService.BuildTrainingRow(forecast, snapshots);

        Assert.NotNull(row);
        Assert.Equal(0.40f, row.CalculatorProbability);
        Assert.True(float.IsNaN(row.StatisticalProbability));
        Assert.True(float.IsNaN(row.BookmakerProbability));
        Assert.Equal(1f, row.HasSnapshot);
        Assert.Equal(0f, row.HasRestDays);
        Assert.True(float.IsNaN(row.AwayRestDays));
    }
}
