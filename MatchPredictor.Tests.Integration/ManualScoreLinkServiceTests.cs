using MatchPredictor.Application.Helpers;
using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ManualScoreLinkServiceTests
{
    [Fact]
    public async Task GetHintsAsync_SurfacesNearMissScrapedScore()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(18);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(date, kickoff, "Real Madrid CF", "Athletic Club Bilbao", "StraightWin", "Home Win");
        context.Predictions.Add(prediction);

        var score = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "Spain LaLiga",
            HomeTeam = "Real Madrid",
            AwayTeam = "Athletic Bilbao",
            Score = "2:1",
            BTTSLabel = true,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.MatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        Assert.True(hints.TryGetValue(prediction.Id, out var hint));
        Assert.Equal("FlashScore", hint!.SourceName);
        Assert.Equal(score.Id, hint.SourceRowId);
        Assert.Equal("2:1", hint.Score);
        Assert.True(hint.Similarity >= ManualScoreLinkService.HintSimilarityFloor);
    }

    [Fact]
    public async Task GetHintsAsync_IgnoresUnrelatedTeamNames()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(17);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(date, kickoff, "Alpha United", "Beta City", "StraightWin", "Home Win", "Test League");
        context.Predictions.Add(prediction);

        var score = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "Test League",
            HomeTeam = "Zeta Rovers",
            AwayTeam = "Omega Town",
            Score = "1:0",
            BTTSLabel = false,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.MatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        Assert.False(hints.ContainsKey(prediction.Id));
    }

    [Fact]
    public async Task ConfirmAsync_AppliesScoreToFixtureMarketsAndSeedsAliases()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(16);
        var date = kickoff.ToString("dd-MM-yyyy");

        var straightWin = CreatePrediction(date, kickoff, "PSG", "Marseille", "StraightWin", "Home Win", "France Ligue 1");
        var btts = CreatePrediction(date, kickoff, "PSG", "Marseille", "BothTeamsScore", "BTTS", "France Ligue 1");
        context.Predictions.AddRange(straightWin, btts);

        var score = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "France Ligue 1",
            HomeTeam = "Paris Saint Germain",
            AwayTeam = "Olympique Marseille",
            Score = "3:1",
            BTTSLabel = true,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.MatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var result = await service.ConfirmAsync(new ManualScoreConfirmRequest
        {
            PredictionId = straightWin.Id,
            SourceName = "FlashScore",
            SourceRowId = score.Id
        });

        Assert.True(result.Success);
        Assert.Equal("3:1", result.ActualScore);
        Assert.Equal(2, result.UpdatedPredictionCount);

        var predictions = await context.Predictions.OrderBy(p => p.PredictionCategory).ToListAsync();
        Assert.All(predictions, prediction => Assert.Equal("3:1", prediction.ActualScore));
        Assert.Contains(predictions, prediction => prediction.ActualOutcome == "Home Win");
        Assert.Contains(predictions, prediction => prediction.ActualOutcome == "BTTS");

        var aliases = await context.TeamAliases.ToListAsync();
        Assert.Contains(aliases, alias =>
            alias.NormalizedAlias == TeamNameNormalizer.NormalizeAlias("Olympique Marseille") &&
            alias.SourceName == "ManualConfirm");
        Assert.Contains(aliases, alias =>
            alias.NormalizedAlias == TeamNameNormalizer.NormalizeAlias("Marseille") &&
            alias.SourceName == "ManualConfirm");
        // PSG ↔ Paris Saint Germain already share a normalizer synonym, so no ManualConfirm alias is required.
    }

    private static ManualScoreLinkService CreateService(ApplicationDbContext context) =>
        new(context, new TeamResolutionService(context), NullLogger<ManualScoreLinkService>.Instance);

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static DateTime GetStartedKickoff(int hour)
    {
        var nowLocal = DateTimeProvider.GetLocalTime();
        var todayKickoff = nowLocal.Date.AddHours(hour);
        return todayKickoff <= nowLocal ? todayKickoff : todayKickoff.AddDays(-1);
    }

    private static Prediction CreatePrediction(
        string date,
        DateTime kickoff,
        string homeTeam,
        string awayTeam,
        string predictionCategory,
        string predictedOutcome,
        string league = "Spain LaLiga")
    {
        var kickoffUtc = kickoff.Kind == DateTimeKind.Utc
            ? kickoff
            : DateTimeProvider.ConvertLocalToUtc(DateTime.SpecifyKind(kickoff, DateTimeKind.Unspecified));
        var kickoffLocal = kickoff.Kind == DateTimeKind.Utc
            ? DateTimeProvider.ConvertUtcToLocal(kickoff)
            : kickoff;

        return new Prediction
        {
            Date = date,
            Time = kickoffLocal.ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(kickoffLocal),
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = FixtureIdentityFactory.Build(
                homeTeam,
                awayTeam,
                league,
                DateOnly.FromDateTime(kickoffLocal),
                TimeOnly.FromDateTime(kickoffLocal),
                kickoffUtc).FixtureKey,
            League = league,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            PredictionCategory = predictionCategory,
            PredictedOutcome = predictedOutcome,
            ConfidenceScore = 0.75m,
            WasPublished = true,
            IsCurrentRevision = true,
            RunLabel = "test",
            RunReason = "test"
        };
    }
}
