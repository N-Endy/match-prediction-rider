using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class FixtureFeatureServiceTests
{
    [Fact]
    public async Task CaptureFeatureSnapshotsAsync_PersistsInternalHistoryFeatures_WhenApiKeyMissing()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var context = new ApplicationDbContext(options);
        var kickoff = DateTime.UtcNow.AddDays(1);
        context.MatchScores.AddRange(
            new MatchScore { HomeTeam = "Aces", AwayTeam = "Bears", League = "League", Score = "2-1", MatchTime = kickoff.AddDays(-7) },
            new MatchScore { HomeTeam = "Cats", AwayTeam = "Aces", League = "League", Score = "0-3", MatchTime = kickoff.AddDays(-14) },
            new MatchScore { HomeTeam = "Bears", AwayTeam = "Dogs", League = "League", Score = "1-1", MatchTime = kickoff.AddDays(-8) },
            new MatchScore { HomeTeam = "Aces", AwayTeam = "Bears", League = "League", Score = "1-0", MatchTime = kickoff.AddDays(-30) });
        await context.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiFootball:ApiKey"] = ""
            })
            .Build();
        var service = new FixtureFeatureService(
            context,
            configuration,
            new StubHttpClientFactory(),
            NullLogger<FixtureFeatureService>.Instance);

        await service.CaptureFeatureSnapshotsAsync(new[]
        {
            new MatchData
            {
                FixtureKey = "league-aces-bears",
                MatchLocalDate = DateOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                League = "League",
                HomeTeam = "Aces",
                AwayTeam = "Bears"
            }
        });

        var snapshot = await context.FixtureFeatureSnapshots.SingleAsync();
        Assert.Equal("InternalHistory", snapshot.SourceName);
        Assert.True(snapshot.HomeRestDays > 0);
        Assert.True(snapshot.HomeFormPointsPerMatch > snapshot.AwayFormPointsPerMatch);
        Assert.Equal(2, snapshot.HeadToHeadHomeWins);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
