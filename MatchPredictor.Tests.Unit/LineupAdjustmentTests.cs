using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Statistics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class LineupAdjustmentTests
{
    private readonly LineupAvailabilityService _service;

    public LineupAdjustmentTests()
    {
        _service = new LineupAvailabilityService(null!, NullLogger<LineupAvailabilityService>.Instance);
    }

    [Fact]
    public void CalculateLineupAdjustment_NoMissingPlayers_ReturnsZeroModifiers()
    {
        var confirmed = new List<string> { "Player1", "Player2", "Player3" };
        var expected = new List<string> { "Player1", "Player2", "Player3" };
        var missing = new List<string>();

        var result = _service.CalculateLineupAdjustment(confirmed, expected, missing);

        Assert.Equal(0.0, result.AttackModifier);
        Assert.Equal(0.0, result.DefenseModifier);
        Assert.Empty(result.DetectedMissingStarters);
    }

    [Fact]
    public void CalculateLineupAdjustment_MissingKeyStriker_DegradesAttackAndDefense()
    {
        var confirmed = new List<string> { "Keeper", "Defender" };
        var expected = new List<string> { "Keeper", "Defender", "StarStriker" };
        var missing = new List<string> { "StarStriker" };

        var result = _service.CalculateLineupAdjustment(confirmed, expected, missing);

        Assert.True(result.AttackModifier < 0, "Missing striker should reduce attack modifier (negative delta)");
        Assert.True(result.DefenseModifier > 0, "Missing starter should degrade defense (positive goals conceded)");
        Assert.Contains("StarStriker", result.DetectedMissingStarters);
    }

    [Fact]
    public void CalculateLineupAdjustment_ClampsToMaxLimit()
    {
        var missing = Enumerable.Range(1, 10).Select(i => $"MissingPlayer{i}").ToList();

        var result = _service.CalculateLineupAdjustment([], [], missing);

        Assert.True(result.AttackModifier >= -0.25);
        Assert.True(result.DefenseModifier <= 0.25);
    }

    [Fact]
    public void DixonColes_WithLineupModifiers_PreservesSimplexAndCoherence()
    {
        var asOf = DateTime.UtcNow;
        var matches = new List<MatchResult>
        {
            new("TeamA", "TeamB", 2, 1, asOf.AddDays(-10), "EPL"),
            new("TeamB", "TeamC", 1, 1, asOf.AddDays(-8), "EPL"),
            new("TeamC", "TeamA", 0, 2, asOf.AddDays(-6), "EPL"),
            new("TeamA", "TeamC", 3, 0, asOf.AddDays(-4), "EPL")
        };

        var model = DixonColesModel.Fit(matches, asOf, new DixonColesOptions { Iterations = 50, MinMatchesPerTeam = 1, MinMatchesPerLeague = 1 });

        // Baseline prediction
        var baseline = model.Predict("TeamA", "TeamB");

        // Prediction with missing star striker on TeamA (negative attack modifier -0.15)
        var adjusted = model.Predict("TeamA", "TeamB", homeAttackModifier: -0.15, homeDefenseModifier: 0.0, awayAttackModifier: 0.0, awayDefenseModifier: 0.0);

        // Expected goals should be lower for TeamA
        var (baseHomeXg, _) = model.ExpectedGoals("TeamA", "TeamB");
        var (adjHomeXg, _) = model.ExpectedGoals("TeamA", "TeamB", homeAttackModifier: -0.15, homeDefenseModifier: 0.0, awayAttackModifier: 0.0, awayDefenseModifier: 0.0);
        Assert.True(adjHomeXg < baseHomeXg, "Adjusted Home xG should be strictly lower when attack is degraded");

        // Simplex axiom: HomeWin + Draw + AwayWin = 1.0
        var probSum = adjusted.HomeWin + adjusted.Draw + adjusted.AwayWin;
        Assert.Equal(1.0, probSum, 4);

        // Complementary coherence: Under2.5 = 1.0 - Over2.5
        Assert.Equal(1.0, adjusted.Over25 + adjusted.Under25, 4);
    }
}
