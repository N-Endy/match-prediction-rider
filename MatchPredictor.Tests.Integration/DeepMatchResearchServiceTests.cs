using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class DeepMatchResearchServiceTests
{
    private readonly ApplicationDbContext _dbContext;
    private readonly FakeFootballInsightService _fakeInsightService;
    private readonly IDistributedCache _cache;
    private readonly DeepMatchResearchService _researchService;

    public DeepMatchResearchServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new ApplicationDbContext(dbOptions);
        _fakeInsightService = new FakeFootballInsightService();

        var memoryOptions = Options.Create(new MemoryDistributedCacheOptions());
        _cache = new MemoryDistributedCache(memoryOptions);

        _researchService = new DeepMatchResearchService(
            _dbContext,
            _fakeInsightService,
            _cache,
            NullLogger<DeepMatchResearchService>.Instance);
    }

    [Fact]
    public async Task ResearchFixtureAsync_SynthesizesExpectedGoalsAndEloRatings()
    {
        // Arrange
        var home = "Liverpool";
        var away = "Everton";
        var date = DateTime.UtcNow;

        _dbContext.Predictions.Add(new Prediction
        {
            Id = 101,
            HomeTeam = home,
            AwayTeam = away,
            Date = date.ToString("dd-MM-yyyy"),
            Time = "15:00",
            MatchLocalDate = DateOnly.FromDateTime(date),
            PredictionCategory = "Straight Win",
            PredictedOutcome = "Home",
            ConfidenceScore = 0.78m,
            League = "Premier League"
        });
        await _dbContext.SaveChangesAsync();

        var insightSnapshot = new FootballMatchInsightSnapshot
        {
            HomeTeam = home,
            AwayTeam = away,
            InsightSource = "InternalDatabase",
            DataQuality = "High",
            HomeForm = new TeamFormSnapshot
            {
                TeamName = home,
                SampleSize = 5,
                PointsPerMatch = 2.4,
                GoalsForPerMatch = 2.6,
                GoalsAgainstPerMatch = 0.8,
                VenueGoalsForPerMatch = 3.0,
                VenueGoalsAgainstPerMatch = 0.6,
                CleanSheetRate = 0.60
            },
            AwayForm = new TeamFormSnapshot
            {
                TeamName = away,
                SampleSize = 5,
                PointsPerMatch = 0.8,
                GoalsForPerMatch = 0.9,
                GoalsAgainstPerMatch = 2.1,
                VenueGoalsForPerMatch = 0.7,
                VenueGoalsAgainstPerMatch = 2.4
            },
            HeadToHead = new HeadToHeadSummary
            {
                SampleSize = 4,
                HomeTeamWins = 3,
                Draws = 1,
                AwayTeamWins = 0
            }
        };

        _fakeInsightService.SnapshotsToReturn[$"{home}_{away}_{date:yyyyMMdd}"] = insightSnapshot;

        // Act
        var dossier = await _researchService.ResearchFixtureAsync(home, away, date, "Premier League");

        // Assert
        Assert.NotNull(dossier);
        Assert.Equal(home, dossier.HomeTeam);
        Assert.Equal(away, dossier.AwayTeam);
        Assert.Equal("High", dossier.ConvictionLevel);
        Assert.Equal(0.78, dossier.CalibratedHomeWinProb);
        Assert.True(dossier.HomeExpectedGoals > dossier.AwayExpectedGoals);
        Assert.True(dossier.HomeEloRating > dossier.AwayEloRating);
        Assert.NotEmpty(dossier.TacticalFactors);
        Assert.Contains(dossier.TacticalFactors, t => t.Contains("Head-to-head", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Match Analysis", dossier.ResearchSummary);
    }

    [Fact]
    public async Task ResearchFixtureAsync_UsesCacheOnSubsequentCalls()
    {
        // Arrange
        var home = "Arsenal";
        var away = "Chelsea";
        var date = DateTime.UtcNow;

        // Act - Call 1 (populates cache)
        var dossier1 = await _researchService.ResearchFixtureAsync(home, away, date);

        // Act - Call 2 (should read from cache without querying insight service again)
        var dossier2 = await _researchService.ResearchFixtureAsync(home, away, date);

        // Assert
        Assert.NotNull(dossier2);
        Assert.Equal(dossier1.FixtureKey, dossier2.FixtureKey);
        Assert.Equal(1, _fakeInsightService.CallCount);
    }

    private class FakeFootballInsightService : IAiChatFootballInsightService
    {
        public Dictionary<string, FootballMatchInsightSnapshot> SnapshotsToReturn { get; set; } = new();
        public int CallCount { get; private set; }

        public Task<IReadOnlyDictionary<string, FootballMatchInsightSnapshot>> GetInsightsAsync(
            IReadOnlyCollection<AiChatFootballInsightRequest> requests,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyDictionary<string, FootballMatchInsightSnapshot>>(SnapshotsToReturn);
        }
    }
}
