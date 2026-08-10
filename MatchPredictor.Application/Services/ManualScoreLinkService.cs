using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

public sealed class ManualScoreLinkService : IManualScoreLinkService
{
    public const double HintSimilarityFloor = 0.50;
    private const double OrientationPreferenceEpsilon = 0.05;
    private static readonly TimeSpan FutureFixtureSettlementTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HintKickoffProximity = TimeSpan.FromHours(36);

    private readonly ApplicationDbContext _dbContext;
    private readonly ITeamResolutionService _teamResolutionService;
    private readonly ILogger<ManualScoreLinkService> _logger;

    public ManualScoreLinkService(
        ApplicationDbContext dbContext,
        ITeamResolutionService teamResolutionService,
        ILogger<ManualScoreLinkService> logger)
    {
        _dbContext = dbContext;
        _teamResolutionService = teamResolutionService;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<int, ScoreNearMissHint>> GetHintsAsync(
        IReadOnlyList<int> predictionIds,
        CancellationToken cancellationToken = default)
    {
        if (predictionIds.Count == 0)
        {
            return new Dictionary<int, ScoreNearMissHint>();
        }

        var distinctIds = predictionIds.Distinct().ToList();
        var predictions = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction => distinctIds.Contains(prediction.Id) && prediction.IsCurrentRevision)
            .ToListAsync(cancellationToken);

        var unscoredEligible = predictions
            .Where(prediction => string.IsNullOrWhiteSpace(prediction.ActualScore))
            .Where(IsEligibleForHint)
            .ToList();

        if (unscoredEligible.Count == 0)
        {
            return new Dictionary<int, ScoreNearMissHint>();
        }

        var aliasLookup = await _teamResolutionService.LoadAliasLookupAsync(cancellationToken);
        var (startOfWindowUtc, endOfWindowUtc, minDate, maxDate) = BuildCandidateWindows(unscoredEligible);

        var flashScores = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => !string.IsNullOrWhiteSpace(score.Score))
            .Where(score =>
                (score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc) ||
                (score.MatchLocalDate != default && score.MatchLocalDate >= minDate && score.MatchLocalDate <= maxDate))
            .ToListAsync(cancellationToken);

        var aiScores = await _dbContext.AiScoreMatchScores
            .AsNoTracking()
            .Where(score => !string.IsNullOrWhiteSpace(score.Score))
            .Where(score =>
                (score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc) ||
                (score.MatchLocalDate != default && score.MatchLocalDate >= minDate && score.MatchLocalDate <= maxDate))
            .ToListAsync(cancellationToken);

        var sofaScores = await _dbContext.SofaScoreMatchScores
            .AsNoTracking()
            .Where(score => !string.IsNullOrWhiteSpace(score.Score) || !string.IsNullOrWhiteSpace(score.DisplayedScore))
            .Where(score =>
                (score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc) ||
                (score.MatchLocalDate != default && score.MatchLocalDate >= minDate && score.MatchLocalDate <= maxDate))
            .ToListAsync(cancellationToken);

        var candidates = flashScores
            .Select(score => ToCandidate(
                "FlashScore",
                score.Id,
                null,
                score.HomeTeam,
                score.AwayTeam,
                score.League,
                score.Score,
                score.IsLive,
                ResolveStoredLocalDate(score.MatchLocalDate, score.MatchTime),
                score.MatchTime))
            .Concat(aiScores.Select(score => ToCandidate(
                "AiScore",
                score.Id,
                score.SourceEventId,
                score.HomeTeam,
                score.AwayTeam,
                score.League,
                score.Score,
                score.IsLive,
                ResolveStoredLocalDate(score.MatchLocalDate, score.MatchTime),
                score.MatchTime)))
            .Concat(sofaScores.Select(score => ToCandidate(
                "SofaScore",
                score.Id,
                score.EventId?.ToString(),
                score.HomeTeam,
                score.AwayTeam,
                score.League,
                string.IsNullOrWhiteSpace(score.DisplayedScore) ? score.Score : score.DisplayedScore!,
                score.IsLive,
                ResolveStoredLocalDate(score.MatchLocalDate, score.MatchTime),
                score.MatchTime)))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Score))
            .ToList();

        var hintsByFixture = new Dictionary<string, ScoreNearMissHint>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<int, ScoreNearMissHint>();

        foreach (var prediction in unscoredEligible)
        {
            var fixtureKey = string.IsNullOrWhiteSpace(prediction.FixtureKey)
                ? FixtureIdentityFactory.FromPrediction(prediction).FixtureKey
                : prediction.FixtureKey;

            if (hintsByFixture.TryGetValue(fixtureKey, out var existing))
            {
                result[prediction.Id] = existing with { PredictionId = prediction.Id };
                continue;
            }

            var hint = FindBestHint(prediction, candidates, aliasLookup);
            if (hint is null)
            {
                continue;
            }

            hintsByFixture[fixtureKey] = hint;
            result[prediction.Id] = hint;
        }

        return result;
    }

    public async Task<ManualScoreConfirmResult> ConfirmAsync(
        ManualScoreConfirmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.PredictionId <= 0 ||
            request.SourceRowId <= 0 ||
            string.IsNullOrWhiteSpace(request.SourceName))
        {
            return new ManualScoreConfirmResult { Success = false, Error = "Invalid confirm request." };
        }

        var seedPrediction = await _dbContext.Predictions
            .FirstOrDefaultAsync(
                prediction => prediction.Id == request.PredictionId && prediction.IsCurrentRevision,
                cancellationToken);

        if (seedPrediction is null)
        {
            return new ManualScoreConfirmResult { Success = false, Error = "Prediction not found." };
        }

        var scraped = await LoadScrapedScoreAsync(request.SourceName, request.SourceRowId, cancellationToken);
        if (scraped is null)
        {
            return new ManualScoreConfirmResult { Success = false, Error = "Scraped score row not found." };
        }

        if (string.IsNullOrWhiteSpace(scraped.Score))
        {
            return new ManualScoreConfirmResult { Success = false, Error = "Scraped score is empty." };
        }

        var aliasLookup = await _teamResolutionService.LoadAliasLookupAsync(cancellationToken);
        var orientation = ResolveOrientation(seedPrediction, scraped.HomeTeam, scraped.AwayTeam, scraped.League, aliasLookup);
        var appliedScore = orientation.IsFlipped ? ReverseScore(scraped.Score) : scraped.Score;
        var aliasScrapedHome = orientation.IsFlipped ? scraped.AwayTeam : scraped.HomeTeam;
        var aliasScrapedAway = orientation.IsFlipped ? scraped.HomeTeam : scraped.AwayTeam;

        var fixtureKey = string.IsNullOrWhiteSpace(seedPrediction.FixtureKey)
            ? FixtureIdentityFactory.FromPrediction(seedPrediction).FixtureKey
            : seedPrediction.FixtureKey;
        var localDate = ResolveLocalDate(seedPrediction);

        var fixturePredictions = await _dbContext.Predictions
            .Where(prediction =>
                prediction.IsCurrentRevision &&
                prediction.MatchLocalDate == localDate &&
                (prediction.FixtureKey == fixtureKey ||
                 (prediction.HomeTeam == seedPrediction.HomeTeam &&
                  prediction.AwayTeam == seedPrediction.AwayTeam &&
                  prediction.League == seedPrediction.League)))
            .ToListAsync(cancellationToken);

        if (fixturePredictions.Count == 0)
        {
            fixturePredictions = [seedPrediction];
        }

        var fixtureForecasts = await _dbContext.ForecastObservations
            .Where(forecast =>
                forecast.IsCurrentRevision &&
                forecast.MatchLocalDate == localDate &&
                ((forecast.FixtureKey == fixtureKey && !string.IsNullOrWhiteSpace(fixtureKey)) ||
                 (forecast.HomeTeam == seedPrediction.HomeTeam &&
                  forecast.AwayTeam == seedPrediction.AwayTeam &&
                  forecast.League == seedPrediction.League)))
            .ToListAsync(cancellationToken);

        foreach (var prediction in fixturePredictions)
        {
            ApplyPredictionSettlement(prediction, appliedScore, scraped.BttsLabel, scraped.IsLive, scraped.SourceName, scraped.SourceEventId);
        }

        foreach (var forecast in fixtureForecasts)
        {
            ApplyForecastSettlement(forecast, appliedScore, scraped.BttsLabel, scraped.IsLive, scraped.SourceName, scraped.SourceEventId);
        }

        await _teamResolutionService.EnsureManualConfirmAliasesAsync(
            seedPrediction.HomeTeam,
            seedPrediction.AwayTeam,
            aliasScrapedHome,
            aliasScrapedAway,
            seedPrediction.League,
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var updates = fixturePredictions
            .Select(prediction => new ManualScoreConfirmPredictionUpdate
            {
                PredictionId = prediction.Id,
                ScoreClass = PredictionScoreClassHelper.GetScoreClass(prediction, nowUtc),
                IsLive = PredictionScoreClassHelper.IsActuallyLive(prediction, nowUtc)
            })
            .ToList();

        _logger.LogInformation(
            "Manual score confirm: prediction {PredictionId} linked to {SourceName}#{SourceRowId} score {Score} (flipped={Flipped}); updated {Count} prediction(s).",
            request.PredictionId,
            scraped.SourceName,
            request.SourceRowId,
            appliedScore,
            orientation.IsFlipped,
            fixturePredictions.Count);

        return new ManualScoreConfirmResult
        {
            Success = true,
            ActualScore = appliedScore,
            IsLive = updates.Any(update => update.IsLive),
            UpdatedPredictionCount = fixturePredictions.Count,
            UpdatedPredictionIds = fixturePredictions.Select(prediction => prediction.Id).ToList(),
            Updates = updates
        };
    }

    private static (DateTime StartUtc, DateTime EndUtc, DateOnly MinDate, DateOnly MaxDate) BuildCandidateWindows(
        IReadOnlyList<Prediction> predictions)
    {
        var localDates = predictions
            .Select(ResolveLocalDate)
            .Where(date => date != default)
            .Distinct()
            .ToList();

        var minDate = localDates.Count == 0
            ? DateTimeProvider.GetLocalDate().AddDays(-2)
            : localDates.Min().AddDays(-1);
        var maxDate = localDates.Count == 0
            ? DateTimeProvider.GetLocalDate().AddDays(1)
            : localDates.Max().AddDays(1);

        var kickoffs = predictions
            .Where(prediction => prediction.MatchDateTime.HasValue)
            .Select(prediction => prediction.MatchDateTime!.Value)
            .ToList();

        DateTime startUtc;
        DateTime endUtc;
        if (kickoffs.Count > 0)
        {
            startUtc = kickoffs.Min().AddDays(-2);
            endUtc = kickoffs.Max().AddDays(2);
        }
        else
        {
            startUtc = DateTimeProvider.ConvertLocalToUtc(
                minDate.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified));
            endUtc = DateTimeProvider.ConvertLocalToUtc(
                maxDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified));
        }

        return (startUtc, endUtc, minDate, maxDate);
    }

    private static ScoreCandidate ToCandidate(
        string sourceName,
        int sourceRowId,
        string? sourceEventId,
        string homeTeam,
        string awayTeam,
        string league,
        string score,
        bool isLive,
        DateOnly matchLocalDate,
        DateTime matchTime) =>
        new(sourceName, sourceRowId, sourceEventId, homeTeam, awayTeam, league, score, isLive, matchLocalDate, matchTime);

    private static ScoreNearMissHint? FindBestHint(
        Prediction prediction,
        IReadOnlyList<ScoreCandidate> candidates,
        IReadOnlyDictionary<string, int> aliasLookup)
    {
        var scored = new List<(ScoreCandidate Candidate, double Similarity, bool IsFlipped, string RejectionHint)>();

        foreach (var candidate in candidates)
        {
            if (!IsWithinHintWindow(prediction, candidate))
            {
                continue;
            }

            var orientation = ResolveOrientation(
                prediction,
                candidate.HomeTeam,
                candidate.AwayTeam,
                candidate.League,
                aliasLookup);

            if (!orientation.Accepted)
            {
                continue;
            }

            scored.Add((candidate, orientation.Similarity, orientation.IsFlipped, orientation.RejectionHint));
        }

        var best = scored
            .OrderByDescending(item => item.Similarity)
            .ThenBy(item => item.IsFlipped)
            .ThenBy(item => item.Candidate.IsLive)
            .FirstOrDefault();

        if (best.Candidate is null)
        {
            return null;
        }

        return new ScoreNearMissHint
        {
            PredictionId = prediction.Id,
            SourceName = best.Candidate.SourceName,
            SourceRowId = best.Candidate.SourceRowId,
            SourceEventId = best.Candidate.SourceEventId,
            ScrapedHomeTeam = best.Candidate.HomeTeam,
            ScrapedAwayTeam = best.Candidate.AwayTeam,
            ScrapedLeague = best.Candidate.League,
            Score = best.Candidate.Score,
            IsLive = best.Candidate.IsLive,
            IsFlipped = best.IsFlipped,
            Similarity = best.Similarity,
            RejectionHint = best.RejectionHint
        };
    }

    private static OrientationScore ResolveOrientation(
        Prediction prediction,
        string scrapedHome,
        string scrapedAway,
        string? scrapedLeague,
        IReadOnlyDictionary<string, int> aliasLookup)
    {
        var normal = ScorePair(
            prediction.HomeTeam,
            prediction.AwayTeam,
            scrapedHome,
            scrapedAway,
            prediction.League,
            scrapedLeague,
            aliasLookup,
            flipped: false);

        var flipped = ScorePair(
            prediction.HomeTeam,
            prediction.AwayTeam,
            scrapedAway,
            scrapedHome,
            prediction.League,
            scrapedLeague,
            aliasLookup,
            flipped: true);

        if (!normal.Accepted && !flipped.Accepted)
        {
            return OrientationScore.Rejected;
        }

        if (normal.Accepted && flipped.Accepted)
        {
            if (flipped.Similarity > normal.Similarity + OrientationPreferenceEpsilon)
            {
                return flipped;
            }

            return normal;
        }

        return normal.Accepted ? normal : flipped;
    }

    private static OrientationScore ScorePair(
        string predictionHome,
        string predictionAway,
        string scrapedHome,
        string scrapedAway,
        string? predictionLeague,
        string? scrapedLeague,
        IReadOnlyDictionary<string, int> aliasLookup,
        bool flipped)
    {
        var homeMatch = TeamAliasMatchHelper.GetTeamMatchResult(
            predictionHome,
            scrapedHome,
            predictionLeague,
            scrapedLeague,
            aliasLookup);
        var awayMatch = TeamAliasMatchHelper.GetTeamMatchResult(
            predictionAway,
            scrapedAway,
            predictionLeague,
            scrapedLeague,
            aliasLookup);

        if (homeMatch.HasQualifierMismatch || awayMatch.HasQualifierMismatch)
        {
            return OrientationScore.Rejected;
        }

        var similarity = (homeMatch.Score + awayMatch.Score) / 2.0;
        if (similarity < HintSimilarityFloor)
        {
            return OrientationScore.Rejected;
        }

        var rejectionHint = !homeMatch.IsMatch || !awayMatch.IsMatch
            ? "NearMiss"
            : similarity < 0.84
                ? "BelowFuzzyFloor"
                : flipped
                    ? "FlippedOrientation"
                    : "AmbiguousOrNearMiss";

        return new OrientationScore(true, flipped, similarity, rejectionHint);
    }

    private static bool IsWithinHintWindow(Prediction prediction, ScoreCandidate candidate)
    {
        if (prediction.MatchDateTime.HasValue)
        {
            var hoursApart = Math.Abs((candidate.MatchTime - prediction.MatchDateTime.Value).TotalHours);
            if (hoursApart <= HintKickoffProximity.TotalHours)
            {
                return true;
            }
        }

        var targetDate = ResolveLocalDate(prediction);
        var candidateDate = candidate.MatchLocalDate != default
            ? candidate.MatchLocalDate
            : DateTimeProvider.ConvertUtcToLocalDate(candidate.MatchTime);

        if (targetDate != default && candidateDate != default)
        {
            return Math.Abs(candidateDate.DayNumber - targetDate.DayNumber) <= 1;
        }

        return false;
    }

    private static DateOnly ResolveStoredLocalDate(DateOnly matchLocalDate, DateTime matchTime)
    {
        if (matchLocalDate != default)
        {
            return matchLocalDate;
        }

        return DateTimeProvider.ConvertUtcToLocalDate(matchTime);
    }

    private async Task<ScrapedScoreRow?> LoadScrapedScoreAsync(
        string sourceName,
        int sourceRowId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(sourceName, "FlashScore", StringComparison.OrdinalIgnoreCase))
        {
            var row = await _dbContext.MatchScores.FirstOrDefaultAsync(score => score.Id == sourceRowId, cancellationToken);
            return row is null
                ? null
                : new ScrapedScoreRow("FlashScore", row.HomeTeam, row.AwayTeam, row.League, row.Score, row.BTTSLabel, row.IsLive, null);
        }

        if (string.Equals(sourceName, "AiScore", StringComparison.OrdinalIgnoreCase))
        {
            var row = await _dbContext.AiScoreMatchScores.FirstOrDefaultAsync(score => score.Id == sourceRowId, cancellationToken);
            return row is null
                ? null
                : new ScrapedScoreRow("AiScore", row.HomeTeam, row.AwayTeam, row.League, row.Score, row.BTTSLabel, row.IsLive, row.SourceEventId);
        }

        if (string.Equals(sourceName, "SofaScore", StringComparison.OrdinalIgnoreCase))
        {
            var row = await _dbContext.SofaScoreMatchScores.FirstOrDefaultAsync(score => score.Id == sourceRowId, cancellationToken);
            if (row is null)
            {
                return null;
            }

            var score = string.IsNullOrWhiteSpace(row.DisplayedScore) ? row.Score : row.DisplayedScore;
            return new ScrapedScoreRow(
                "SofaScore",
                row.HomeTeam,
                row.AwayTeam,
                row.League,
                score,
                row.BTTSLabel,
                row.IsLive,
                row.EventId?.ToString());
        }

        return null;
    }

    private static bool IsEligibleForHint(Prediction prediction)
    {
        var nowUtc = DateTime.UtcNow;
        if (prediction.MatchDateTime.HasValue)
        {
            return prediction.MatchDateTime.Value <= nowUtc + FutureFixtureSettlementTolerance;
        }

        var today = DateTimeProvider.GetLocalDate();
        var localDate = ResolveLocalDate(prediction);
        return localDate != default && localDate <= today;
    }

    private static DateOnly ResolveLocalDate(Prediction prediction)
    {
        if (prediction.MatchLocalDate != default)
        {
            return prediction.MatchLocalDate;
        }

        return DateTimeProvider.ParseLocalDateOrNull(prediction.Date) ?? default;
    }

    private static string ReverseScore(string score)
    {
        if (!TryParseScore(score, out var home, out var away))
        {
            return score;
        }

        var separator = score.Contains(':', StringComparison.Ordinal) ? ":" : "-";
        return $"{away}{separator}{home}";
    }

    private static void ApplyPredictionSettlement(
        Prediction prediction,
        string score,
        bool bttsLabel,
        bool isLive,
        string sourceName,
        string? sourceEventId)
    {
        var effectiveIsLive = DetermineEffectivePredictionIsLive(prediction, score, bttsLabel, isLive);
        prediction.ActualScore = score;
        prediction.IsLive = effectiveIsLive;
        prediction.SettledSourceName = sourceName;
        prediction.SettledSourceEventId = sourceEventId;
        prediction.ActualOutcome = effectiveIsLive
            ? null
            : DeterminePredictionActualOutcome(prediction.PredictionCategory, score, bttsLabel);
    }

    private static void ApplyForecastSettlement(
        ForecastObservation forecast,
        string score,
        bool bttsLabel,
        bool isLive,
        string sourceName,
        string? sourceEventId)
    {
        var effectiveIsLive = DetermineEffectiveForecastIsLive(forecast, score, bttsLabel, isLive);
        forecast.ActualScore = score;
        forecast.IsLive = effectiveIsLive;
        forecast.SettledSourceName = sourceName;
        forecast.SettledSourceEventId = sourceEventId;

        if (effectiveIsLive)
        {
            forecast.IsSettled = false;
            forecast.OutcomeOccurred = null;
            forecast.ActualOutcome = null;
            forecast.SettledAt = null;
            return;
        }

        forecast.IsSettled = true;
        forecast.SettledAt = DateTime.UtcNow;
        forecast.OutcomeOccurred = DetermineForecastOutcomeOccurred(forecast.Market, score, bttsLabel);
        forecast.ActualOutcome = DetermineForecastActualOutcome(forecast.Market, score, bttsLabel);
    }

    private static bool DetermineEffectivePredictionIsLive(
        Prediction prediction,
        string score,
        bool? bttsLabel,
        bool sourceIsLive)
    {
        if (!sourceIsLive)
        {
            return false;
        }

        return PredictionMarketExtensions.TryFromCategory(prediction.PredictionCategory, out var market) &&
               CanSettleMarketEarly(market, score, bttsLabel)
            ? false
            : sourceIsLive;
    }

    private static bool DetermineEffectiveForecastIsLive(
        ForecastObservation forecast,
        string score,
        bool? bttsLabel,
        bool sourceIsLive)
    {
        if (!sourceIsLive)
        {
            return false;
        }

        return CanSettleMarketEarly(forecast.Market, score, bttsLabel)
            ? false
            : sourceIsLive;
    }

    private static bool CanSettleMarketEarly(PredictionMarket market, string score, bool? bttsLabel)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => HasBothTeamsScored(score, bttsLabel),
            PredictionMarket.Over25Goals => HasOver25BeenMet(score),
            _ => false
        };
    }

    private static bool HasBothTeamsScored(string score, bool? fallbackBttsLabel)
    {
        if (TryParseScore(score, out var homeGoals, out var awayGoals))
        {
            return homeGoals > 0 && awayGoals > 0;
        }

        return fallbackBttsLabel == true;
    }

    private static bool HasOver25BeenMet(string score) =>
        TryParseScore(score, out var homeGoals, out var awayGoals) && homeGoals + awayGoals > 2;

    private static string? DeterminePredictionActualOutcome(string predictionCategory, string score, bool? bttsLabel) =>
        predictionCategory switch
        {
            "BothTeamsScore" => DetermineBttsOutcome(score, bttsLabel),
            "Draw" => DetermineDrawOutcome(score),
            "Over2.5Goals" => DetermineOver25Outcome(score),
            "Under2.5Goals" => DetermineOver25Outcome(score),
            "StraightWin" => DetermineStraightWinOutcome(score),
            _ => null
        };

    private static bool? DetermineForecastOutcomeOccurred(PredictionMarket market, string score, bool bttsLabel) =>
        market switch
        {
            PredictionMarket.BothTeamsScore => TryParseScore(score, out var h, out var a)
                ? h > 0 && a > 0
                : bttsLabel,
            PredictionMarket.Over25Goals => TryParseScore(score, out var ho, out var ao) ? ho + ao > 2 : null,
            PredictionMarket.Under25Goals => TryParseScore(score, out var hu, out var au) ? hu + au <= 2 : null,
            PredictionMarket.Draw => TryParseScore(score, out var hd, out var ad) ? hd == ad : null,
            PredictionMarket.HomeWin => TryParseScore(score, out var hw, out var aw) ? hw > aw : null,
            PredictionMarket.AwayWin => TryParseScore(score, out var ha, out var aa) ? aa > ha : null,
            PredictionMarket.StraightWin =>
                DetermineStraightWinOutcome(score) is "Home Win" or "Away Win",
            _ => null
        };

    private static string? DetermineForecastActualOutcome(PredictionMarket market, string score, bool bttsLabel) =>
        market switch
        {
            PredictionMarket.BothTeamsScore => (DetermineForecastOutcomeOccurred(market, score, bttsLabel) ?? false) ? "BTTS" : "No BTTS",
            PredictionMarket.Over25Goals => DetermineOver25Outcome(score),
            PredictionMarket.Under25Goals => DetermineOver25Outcome(score),
            PredictionMarket.Draw => DetermineDrawOutcome(score),
            PredictionMarket.HomeWin => (DetermineForecastOutcomeOccurred(market, score, bttsLabel) ?? false) ? "Home Win" : "Not Home Win",
            PredictionMarket.AwayWin => (DetermineForecastOutcomeOccurred(market, score, bttsLabel) ?? false) ? "Away Win" : "Not Away Win",
            PredictionMarket.StraightWin => DetermineStraightWinOutcome(score),
            _ => null
        };

    private static string DetermineBttsOutcome(string score, bool? fallbackBttsLabel)
    {
        if (TryParseScore(score, out var homeGoals, out var awayGoals))
        {
            return homeGoals > 0 && awayGoals > 0 ? "BTTS" : "No BTTS";
        }

        return fallbackBttsLabel switch
        {
            true => "BTTS",
            false => "No BTTS",
            null => "Unknown"
        };
    }

    private static string DetermineDrawOutcome(string score) =>
        TryParseScore(score, out var h, out var a) ? (h == a ? "Draw" : "Not Draw") : "Unknown";

    private static string DetermineOver25Outcome(string score) =>
        TryParseScore(score, out var h, out var a) ? (h + a > 2 ? "Over 2.5" : "Under 2.5") : "Unknown";

    private static string DetermineStraightWinOutcome(string score)
    {
        if (!TryParseScore(score, out var h, out var a))
        {
            return "Unknown";
        }

        if (h > a)
        {
            return "Home Win";
        }

        return h < a ? "Away Win" : "Draw";
    }

    private static bool TryParseScore(string score, out int home, out int away)
    {
        home = away = 0;
        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var normalized = score.Replace("–", "-").Replace("—", "-").Trim();
        var parts = normalized.Contains(':')
            ? normalized.Split(':', StringSplitOptions.TrimEntries)
            : normalized.Split('-', StringSplitOptions.TrimEntries);

        return parts.Length == 2 &&
               int.TryParse(parts[0].Trim(), out home) &&
               int.TryParse(parts[1].Trim(), out away);
    }

    private sealed record ScoreCandidate(
        string SourceName,
        int SourceRowId,
        string? SourceEventId,
        string HomeTeam,
        string AwayTeam,
        string League,
        string Score,
        bool IsLive,
        DateOnly MatchLocalDate,
        DateTime MatchTime);

    private sealed record ScrapedScoreRow(
        string SourceName,
        string HomeTeam,
        string AwayTeam,
        string League,
        string Score,
        bool BttsLabel,
        bool IsLive,
        string? SourceEventId);

    private readonly record struct OrientationScore(bool Accepted, bool IsFlipped, double Similarity, string RejectionHint)
    {
        public static OrientationScore Rejected => new(false, false, 0, string.Empty);
    }
}
