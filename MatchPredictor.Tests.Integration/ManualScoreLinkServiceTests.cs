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

        var hint = AssertSingleHint(hints, prediction.Id);
        Assert.Equal("FlashScore", hint.SourceName);
        Assert.Equal(score.Id, hint.SourceRowId);
        Assert.Equal("2:1", hint.Score);
        Assert.False(hint.IsFlipped);
        Assert.True(hint.Similarity >= ManualScoreLinkService.HintSimilarityFloor);
    }

    [Fact]
    public async Task GetHintsAsync_SurfacesWhenHomeAwayAreSwapped()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(15);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "Juventud",
            "Defensor Sporting",
            "StraightWin",
            "Home Win",
            "Uruguay Primera Division");
        context.Predictions.Add(prediction);

        var score = new AiScoreMatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "Uruguay Primera Division",
            HomeTeam = "Defensor Sporting",
            AwayTeam = "CA Juventud",
            Score = "1:2",
            SourceEventId = "aiscore-juventud-defensor",
            BTTSLabel = true,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.AiScoreMatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        var hint = AssertSingleHint(hints, prediction.Id);
        Assert.Equal("AiScore", hint.SourceName);
        Assert.True(hint.IsFlipped);
        Assert.Equal("1:2", hint.Score);
        Assert.Equal(score.Id, hint.SourceRowId);
    }

    [Fact]
    public async Task GetHintsAsync_UsesMatchTimeWhenMatchLocalDateIsDefault()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(14);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "NK Brezice",
            "Maribor",
            "Over2.5Goals",
            "Over 2.5",
            "Slovenia Prva Liga");
        context.Predictions.Add(prediction);

        var score = new AiScoreMatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            MatchLocalDate = default,
            League = "Slovenia Prva Liga",
            HomeTeam = "NK Brezice",
            AwayTeam = "NK Maribor",
            Score = "0:3",
            SourceEventId = "aiscore-brezice-maribor",
            BTTSLabel = false,
            IsLive = false
        };
        context.AiScoreMatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        var hint = AssertSingleHint(hints, prediction.Id);
        Assert.Equal("AiScore", hint.SourceName);
        Assert.Equal(score.Id, hint.SourceRowId);
        Assert.Equal("0:3", hint.Score);
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
    public async Task GetHintsAsync_RejectsOneSidedPenarolMatchFromWrongLeague()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(11);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "Penarol",
            "Cerro Largo",
            "StraightWin",
            "Home Win",
            "Uruguay Reserve League");
        context.Predictions.Add(prediction);

        var wrongScore = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "BRAZIL: Amazonense 2",
            HomeTeam = "Operario Esporte Clube",
            AwayTeam = "Penarol",
            Score = "4:0",
            BTTSLabel = false,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(wrongScore);
        context.MatchScores.Add(wrongScore);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        Assert.False(hints.ContainsKey(prediction.Id));
    }

    [Fact]
    public async Task GetHintsAsync_RejectsOneSidedDefensorVsWanderers()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(10);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "Juventud",
            "Defensor Sporting",
            "StraightWin",
            "Home Win",
            "Uruguay Reserve League");
        context.Predictions.Add(prediction);

        var wrongScore = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "URUGUAY: Liga AUF Uruguaya - Clausura",
            HomeTeam = "Defensor Sp.",
            AwayTeam = "Wanderers",
            Score = "0:1",
            BTTSLabel = false,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(wrongScore);
        context.MatchScores.Add(wrongScore);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        Assert.False(hints.ContainsKey(prediction.Id));
    }

    [Fact]
    public async Task GetHintsAsync_AcceptsReserveSidesWhenLeagueIsReserve()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(9);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "Juventud",
            "Defensor Sporting",
            "StraightWin",
            "Home Win",
            "Uruguay Reserve League");
        context.Predictions.Add(prediction);

        var score = new AiScoreMatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "Uruguay: Reserve League",
            HomeTeam = "Juventud De Las Piedras Reserves",
            AwayTeam = "Defensor Sporting Reserve",
            Score = "2:1",
            SourceEventId = "aiscore-juventud-defensor-res",
            BTTSLabel = true,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.AiScoreMatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        var hint = AssertSingleHint(hints, prediction.Id);
        Assert.Equal(score.Id, hint.SourceRowId);
        Assert.Equal("2:1", hint.Score);
        Assert.False(hint.IsFlipped);
    }

    [Fact]
    public async Task GetHintsAsync_AcceptsU19PredictionAgainstU19U20AiScoreSides()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(19);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "NK Brezice",
            "Maribor",
            "StraightWin",
            "Away Win",
            "SLOVENIA - U19 LEAGUE");
        context.Predictions.Add(prediction);

        var score = new AiScoreMatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "Slovenia: U19",
            HomeTeam = "NK Brezice U19",
            AwayTeam = "NK Maribor U20",
            Score = "0:2",
            SourceEventId = "aiscore-brezice-u19-maribor-u20",
            BTTSLabel = false,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.AiScoreMatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        var hint = AssertSingleHint(hints, prediction.Id);
        Assert.Equal(score.Id, hint.SourceRowId);
        Assert.Equal("0:2", hint.Score);
        Assert.False(hint.IsFlipped);
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
    }

    [Fact]
    public async Task ConfirmAsync_ReversesScoreWhenSourceOrientationIsFlipped()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(13);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "Juventud",
            "Defensor Sporting",
            "StraightWin",
            "Home Win",
            "Uruguay Primera Division");
        context.Predictions.Add(prediction);

        // Source shows Defensor 1 - Juventud 2 (home/away flipped vs prediction).
        var score = new AiScoreMatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "Uruguay Primera Division",
            HomeTeam = "Defensor Sporting",
            AwayTeam = "CA Juventud",
            Score = "1:2",
            SourceEventId = "aiscore-flip-confirm",
            BTTSLabel = true,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.AiScoreMatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var result = await service.ConfirmAsync(new ManualScoreConfirmRequest
        {
            PredictionId = prediction.Id,
            SourceName = "AiScore",
            SourceRowId = score.Id
        });

        Assert.True(result.Success);
        Assert.Equal("2:1", result.ActualScore);

        var settled = await context.Predictions.SingleAsync();
        Assert.Equal("2:1", settled.ActualScore);
        Assert.Equal("Home Win", settled.ActualOutcome);
    }

    [Fact]
    public async Task ConfirmAsync_ReturnsCorrectAndIncorrectScoreClassesPerMarket()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(12);
        var date = kickoff.ToString("dd-MM-yyyy");

        var straightWin = CreatePrediction(
            date,
            kickoff,
            "Alpha FC",
            "Beta FC",
            "StraightWin",
            "Home Win",
            "Test League");
        var btts = CreatePrediction(
            date,
            kickoff,
            "Alpha FC",
            "Beta FC",
            "BothTeamsScore",
            "BTTS",
            "Test League");
        context.Predictions.AddRange(straightWin, btts);

        // Away wins 0:2 — StraightWin Home Win is wrong; BTTS is wrong (no both teams scored).
        // Use 1:2 so BTTS correct and StraightWin Home Win incorrect.
        var score = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "Test League",
            HomeTeam = "Alpha FC",
            AwayTeam = "Beta FC",
            Score = "1:2",
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
        Assert.Equal(2, result.Updates.Count);

        var straightUpdate = Assert.Single(result.Updates, update => update.PredictionId == straightWin.Id);
        var bttsUpdate = Assert.Single(result.Updates, update => update.PredictionId == btts.Id);

        Assert.Equal("mp-score-incorrect", straightUpdate.ScoreClass);
        Assert.Equal("mp-score-correct", bttsUpdate.ScoreClass);
        Assert.False(straightUpdate.IsLive);
        Assert.False(bttsUpdate.IsLive);
    }

    [Fact]
    public async Task GetHintsAsync_SurfacesKievKyivTransliterationAsCandidate()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(16);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "Agrobiznes Volochisk",
            "Lokomotiv Kiev",
            "StraightWin",
            "Home Win",
            "UKRAINE - PERSHA LIGA");
        context.Predictions.Add(prediction);

        var score = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "UKRAINE: Persha Liga",
            HomeTeam = "Ahrobiznes Volochysk",
            AwayTeam = "Lokomotyv Kyiv",
            Score = "0:0",
            BTTSLabel = false,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.MatchScores.Add(score);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        var hint = AssertSingleHint(hints, prediction.Id);
        Assert.Equal(score.Id, hint.SourceRowId);
        Assert.Equal("0:0", hint.Score);
        Assert.False(hint.IsFlipped);
        Assert.True(hint.Similarity >= ManualScoreLinkService.HintReviewSimilarityFloor);
    }

    [Fact]
    public async Task GetHintsAsync_IncludesReviewBandLookalikeThatFailsStrictSideFloor()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(15);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "Agrobiznes Volochisk",
            "Lokomotiv Perm",
            "StraightWin",
            "Home Win",
            "UKRAINE - PERSHA LIGA");
        context.Predictions.Add(prediction);

        var score = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "UKRAINE: Persha Liga",
            HomeTeam = "Ahrobiznes Volochysk",
            AwayTeam = "Lokomotyv Parm",
            Score = "1:0",
            BTTSLabel = false,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(score);
        context.MatchScores.Add(score);
        await context.SaveChangesAsync();

        var awayMatch = ScoreMatchingHelper.GetTeamMatchResultForAdminHint(
            "Lokomotiv Perm",
            "Lokomotyv Parm",
            "UKRAINE - PERSHA LIGA",
            "UKRAINE: Persha Liga");
        Assert.True(awayMatch.Score < ManualScoreLinkService.HintMinSideScore);
        Assert.True(awayMatch.Score >= ManualScoreLinkService.HintReviewMinSideScore);

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        var hint = AssertSingleHint(hints, prediction.Id);
        Assert.Equal(score.Id, hint.SourceRowId);
        Assert.Equal("ReviewBand", hint.RejectionHint);
        Assert.True(hint.Similarity >= ManualScoreLinkService.HintReviewSimilarityFloor);
        Assert.True(hint.Similarity < ManualScoreLinkService.HintSimilarityFloor);
    }

    [Fact]
    public async Task GetHintsAsync_ListsMultipleReviewCandidatesForAdminPicker()
    {
        await using var context = CreateContext();
        var kickoff = GetStartedKickoff(14);
        var date = kickoff.ToString("dd-MM-yyyy");

        var prediction = CreatePrediction(
            date,
            kickoff,
            "Agrobiznes Volochisk",
            "Lokomotiv Kiev",
            "StraightWin",
            "Home Win",
            "UKRAINE - PERSHA LIGA");
        context.Predictions.Add(prediction);

        var flash = new MatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "UKRAINE: Persha Liga",
            HomeTeam = "Ahrobiznes Volochysk",
            AwayTeam = "Lokomotyv Kyiv",
            Score = "0:0",
            BTTSLabel = false,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(flash);
        context.MatchScores.Add(flash);

        var ai = new AiScoreMatchScore
        {
            MatchTime = DateTimeProvider.ConvertLocalToUtc(kickoff),
            League = "Ukraine Persha Liga",
            HomeTeam = "FC Agrobiznes Volochisk",
            AwayTeam = "FC Lokomotiv Kyiv",
            Score = "0-0",
            SourceEventId = "aiscore-agro-loko",
            BTTSLabel = false,
            IsLive = false
        };
        ScoreSnapshotKeyFactory.Apply(ai);
        context.AiScoreMatchScores.Add(ai);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var hints = await service.GetHintsAsync([prediction.Id]);

        Assert.True(hints.TryGetValue(prediction.Id, out var set));
        Assert.True(set!.Candidates.Count >= 2);
        Assert.Contains(set.Candidates, candidate => candidate.SourceName == "FlashScore" && candidate.SourceRowId == flash.Id);
        Assert.Contains(set.Candidates, candidate => candidate.SourceName == "AiScore" && candidate.SourceRowId == ai.Id);
    }

    private static ScoreNearMissHint AssertSingleHint(
        IReadOnlyDictionary<int, ScoreNearMissHintSet> hints,
        int predictionId)
    {
        Assert.True(hints.TryGetValue(predictionId, out var set));
        return Assert.Single(set!.Candidates);
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
