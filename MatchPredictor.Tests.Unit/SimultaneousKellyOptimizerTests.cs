using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class SimultaneousKellyOptimizerTests
{
    [Fact]
    public void OptimizeSimultaneousKellyStakes_SingleCandidate_ReturnsUncappedStake()
    {
        var kickoff = new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc);
        var candidates = new List<SimultaneousKellyCandidate>
        {
            new("pick-1", "fixture-1", 0.60, 2.0, kickoff, 0.10, 0.20)
        };

        var allocations = BetPricingMath.OptimizeSimultaneousKellyStakes(candidates);

        Assert.True(allocations.ContainsKey("pick-1"));
        var alloc = allocations["pick-1"];
        Assert.Equal(0.05, alloc.StandaloneStakeFraction, 4);
        Assert.Equal(0.05, alloc.PortfolioStakeFraction, 4);
        Assert.False(alloc.WasCapped);
        Assert.False(alloc.ExcludedDueToFixtureExclusivity);
        Assert.Equal(1, alloc.WindowConcurrentBetCount);
    }

    [Fact]
    public void OptimizeSimultaneousKellyStakes_MultipleConcurrentBets_CapsAggregateExposureTo20Percent()
    {
        var kickoff = new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc);
        var candidates = new List<SimultaneousKellyCandidate>();

        // 8 concurrent bets, each having 5% standalone Kelly -> total standalone 40%.
        for (int i = 1; i <= 8; i++)
        {
            candidates.Add(new SimultaneousKellyCandidate(
                $"pick-{i}",
                $"fixture-{i}",
                0.60,
                2.0,
                kickoff.AddMinutes(i), // within 45m window
                0.10,
                0.20));
        }

        var options = new SimultaneousKellyOptions(MaxWindowExposureFraction: 0.20);
        var allocations = BetPricingMath.OptimizeSimultaneousKellyStakes(candidates, options);

        Assert.Equal(8, allocations.Count);
        var totalAllocated = allocations.Values.Sum(a => a.PortfolioStakeFraction);
        Assert.True(totalAllocated <= 0.2001, $"Expected total portfolio stake <= 0.20, but was {totalAllocated}");
        Assert.True(totalAllocated >= 0.19, $"Expected total portfolio stake to utilize budget near 0.20, but was {totalAllocated}");

        foreach (var alloc in allocations.Values)
        {
            Assert.True(alloc.WasCapped);
            Assert.False(alloc.ExcludedDueToFixtureExclusivity);
            Assert.Equal(8, alloc.WindowConcurrentBetCount);
        }
    }

    [Fact]
    public void OptimizeSimultaneousKellyStakes_StrictFixtureExclusivity_ExcludesSubordinatePickOnSameFixture()
    {
        var kickoff = new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc);
        var candidates = new List<SimultaneousKellyCandidate>
        {
            // Pick 1: Home Win on Fixture A with higher EV (0.25)
            new("pick-A-1X2", "Arsenal|Chelsea|EPL", 0.62, 2.0, kickoff, 0.12, 0.24),
            // Pick 2: Over 2.5 on the same Fixture A with lower EV (0.10)
            new("pick-A-O25", "Arsenal|Chelsea|EPL", 0.55, 2.0, kickoff, 0.05, 0.10),
            // Pick 3: Straight Win on Fixture B
            new("pick-B-1X2", "Liverpool|Everton|EPL", 0.65, 1.8, kickoff, 0.10, 0.17)
        };

        var options = new SimultaneousKellyOptions(EnforceFixtureExclusivity: true);
        var allocations = BetPricingMath.OptimizeSimultaneousKellyStakes(candidates, options);

        Assert.True(allocations.ContainsKey("pick-A-1X2"));
        Assert.True(allocations.ContainsKey("pick-A-O25"));
        Assert.True(allocations.ContainsKey("pick-B-1X2"));

        var allocA1 = allocations["pick-A-1X2"];
        var allocAO25 = allocations["pick-A-O25"];
        var allocB = allocations["pick-B-1X2"];

        // The superior pick on Fixture A is active
        Assert.False(allocA1.ExcludedDueToFixtureExclusivity);
        Assert.True(allocA1.PortfolioStakeFraction > 0);

        // The duplicate pick on Fixture A is excluded
        Assert.True(allocAO25.ExcludedDueToFixtureExclusivity);
        Assert.Equal(0.0, allocAO25.PortfolioStakeFraction);
        Assert.True(allocAO25.StandaloneStakeFraction > 0);

        // Fixture B is active
        Assert.False(allocB.ExcludedDueToFixtureExclusivity);
        Assert.True(allocB.PortfolioStakeFraction > 0);
    }

    [Fact]
    public void OptimizeSimultaneousKellyStakes_SeparateKickoffWindows_EvaluatesSeparately()
    {
        var noonKickoff = new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc);
        var eveningKickoff = new DateTime(2026, 10, 3, 17, 30, 0, DateTimeKind.Utc);

        var candidates = new List<SimultaneousKellyCandidate>
        {
            new("pick-noon-1", "fix-1", 0.60, 2.0, noonKickoff, 0.10, 0.20),
            new("pick-noon-2", "fix-2", 0.60, 2.0, noonKickoff, 0.10, 0.20),
            new("pick-eve-1", "fix-3", 0.60, 2.0, eveningKickoff, 0.10, 0.20)
        };

        var options = new SimultaneousKellyOptions(WindowToleranceMinutes: 45.0, MaxWindowExposureFraction: 0.20);
        var allocations = BetPricingMath.OptimizeSimultaneousKellyStakes(candidates, options);

        // Noon window has 2 concurrent bets (total standalone = 10% <= 20%, uncapped)
        Assert.Equal(2, allocations["pick-noon-1"].WindowConcurrentBetCount);
        Assert.False(allocations["pick-noon-1"].WasCapped);
        Assert.Equal(0.05, allocations["pick-noon-1"].PortfolioStakeFraction, 4);

        // Evening window has 1 bet
        Assert.Equal(1, allocations["pick-eve-1"].WindowConcurrentBetCount);
        Assert.False(allocations["pick-eve-1"].WasCapped);
        Assert.Equal(0.05, allocations["pick-eve-1"].PortfolioStakeFraction, 4);
    }

    [Fact]
    public void OptimizeSimultaneousKellyStakes_RollingRiskWindow_GroupsSequentialFixtures()
    {
        // Fixture A: 14:00, Fixture B: 14:35 (35m later), Fixture C: 15:10 (35m later, 70m from A)
        var kickoffA = new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc);
        var kickoffB = new DateTime(2026, 10, 3, 14, 35, 0, DateTimeKind.Utc);
        var kickoffC = new DateTime(2026, 10, 3, 15, 10, 0, DateTimeKind.Utc);

        var candidates = new List<SimultaneousKellyCandidate>
        {
            new("pick-A", "fix-A", 0.60, 2.0, kickoffA, 0.10, 0.20),
            new("pick-B", "fix-B", 0.60, 2.0, kickoffB, 0.10, 0.20),
            new("pick-C", "fix-C", 0.60, 2.0, kickoffC, 0.10, 0.20)
        };

        var options = new SimultaneousKellyOptions(WindowToleranceMinutes: 45.0, MaxWindowExposureFraction: 0.20);
        var allocations = BetPricingMath.OptimizeSimultaneousKellyStakes(candidates, options);

        // Under rolling window clustering, all 3 sequential fixtures belong to the same concurrent window (count = 3)
        Assert.Equal(3, allocations["pick-A"].WindowConcurrentBetCount);
        Assert.Equal(3, allocations["pick-B"].WindowConcurrentBetCount);
        Assert.Equal(3, allocations["pick-C"].WindowConcurrentBetCount);
    }
}
