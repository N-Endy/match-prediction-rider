using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class HistoricalDatasetTests
{
    [Fact]
    public async Task DatasetService_ExportThenImportIntoFreshStore_RestoresFinishedMatches()
    {
        var json = await ExportFromSeededStore();

        await using var target = CreateContext();
        var service = new HistoricalDatasetService(target);
        var imported = await service.ImportAsync(json, replaceExisting: false);

        Assert.Equal(2, imported);
        Assert.Equal(2, await target.MatchScores.CountAsync());
    }

    [Fact]
    public async Task DatasetService_Import_WithoutReplace_SkipsDuplicateFixtures()
    {
        var json = await ExportFromSeededStore();

        await using var target = CreateContext();
        var service = new HistoricalDatasetService(target);

        var first = await service.ImportAsync(json, replaceExisting: false);
        var second = await service.ImportAsync(json, replaceExisting: false);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        Assert.Equal(2, await target.MatchScores.CountAsync());
    }

    private static async Task<string> ExportFromSeededStore()
    {
        await using var source = CreateContext();
        source.MatchScores.AddRange(
            BuildScore("EPL", "Arsenal", "Chelsea", "2:1", DateTime.UtcNow.AddDays(-3)),
            BuildScore("EPL", "Liverpool", "Everton", "0:0", DateTime.UtcNow.AddDays(-2)),
            BuildScore("EPL", "City", "United", "1:1", DateTime.UtcNow.AddMinutes(-30), isLive: true));
        await source.SaveChangesAsync();

        var exportService = new HistoricalDatasetService(source);
        return await exportService.ExportAsync(lookbackDays: 30);
    }

    private static MatchScore BuildScore(
        string league,
        string home,
        string away,
        string score,
        DateTime matchTimeUtc,
        bool btts = false,
        bool isLive = false)
    {
        return new MatchScore
        {
            League = league,
            HomeTeam = home,
            AwayTeam = away,
            Score = score,
            MatchTime = matchTimeUtc,
            BTTSLabel = btts,
            IsLive = isLive
        };
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }
}
