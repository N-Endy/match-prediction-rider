using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class FeatureDriftMonitorServiceTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task ComputeDriftReportAsync_EmptyDatabase_ReturnsEmptyResults()
    {
        await using var context = CreateContext();
        var service = new FeatureDriftMonitorService(context, NullLogger<FeatureDriftMonitorService>.Instance);

        var report = await service.ComputeDriftReportAsync(referenceWindowDays: 90, currentWindowDays: 30);

        Assert.NotNull(report);
        Assert.Empty(report.FeatureResults);
        Assert.False(report.HasSignificantDrift);
        Assert.Equal(0, report.ReferenceSnapshotCount);
        Assert.Equal(0, report.CurrentSnapshotCount);
    }

    [Fact]
    public async Task ComputeDriftReportAsync_WithShiftedDistributions_DetectsDrift()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        // Seed 30 reference snapshots (45 days ago) with lower xG
        for (var i = 0; i < 30; i++)
        {
            context.FixtureFeatureSnapshots.Add(new FixtureFeatureSnapshot
            {
                FixtureKey = $"fixture-ref-{i}",
                CapturedAtUtc = now.AddDays(-45).AddHours(i),
                HomeExpectedGoalsFor = 1.1 + (i % 3) * 0.1,
                AwayExpectedGoalsFor = 0.9 + (i % 3) * 0.1,
                HomeFormGoalsForPerMatch = 1.0,
                AwayFormGoalsForPerMatch = 0.8,
                HomeFormGoalsAgainstPerMatch = 1.0,
                AwayFormGoalsAgainstPerMatch = 1.1,
                HomeRestDays = 7,
                AwayRestDays = 7
            });
        }

        // Seed 30 current snapshots (5 days ago) with significantly higher xG
        for (var i = 0; i < 30; i++)
        {
            context.FixtureFeatureSnapshots.Add(new FixtureFeatureSnapshot
            {
                FixtureKey = $"fixture-cur-{i}",
                CapturedAtUtc = now.AddDays(-5).AddHours(i),
                HomeExpectedGoalsFor = 2.8 + (i % 3) * 0.2,
                AwayExpectedGoalsFor = 2.5 + (i % 3) * 0.2,
                HomeFormGoalsForPerMatch = 2.5,
                AwayFormGoalsForPerMatch = 2.2,
                HomeFormGoalsAgainstPerMatch = 1.3,
                AwayFormGoalsAgainstPerMatch = 1.4,
                HomeRestDays = 3,
                AwayRestDays = 3
            });
        }

        await context.SaveChangesAsync();

        var service = new FeatureDriftMonitorService(context, NullLogger<FeatureDriftMonitorService>.Instance);
        var report = await service.ComputeDriftReportAsync(referenceWindowDays: 90, currentWindowDays: 30);

        Assert.NotNull(report);
        Assert.NotEmpty(report.FeatureResults);
        Assert.Equal(30, report.ReferenceSnapshotCount);
        Assert.Equal(30, report.CurrentSnapshotCount);

        var homeXgResult = report.FeatureResults.FirstOrDefault(r => r.FeatureName == "HomeExpectedGoalsFor");
        Assert.NotNull(homeXgResult);
        Assert.True(homeXgResult.WassersteinDistance > 0.5);
    }
}
