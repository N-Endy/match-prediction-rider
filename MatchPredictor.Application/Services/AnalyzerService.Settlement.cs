using System.Globalization;
using System.Text.Json;
using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Sourcing;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MatchPredictor.Application.Services;

public partial class AnalyzerService
{
    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(AnalyzerJobResource, 1800)]
    public async Task RunScoreUpdaterAsync(int lookbackDays = RecentScoreUpdaterLookbackDays, string runLabel = "recent")
    {
        var normalizedLookbackDays = Math.Clamp(lookbackDays, 0, HistoricalScoreBackfillLookbackDays);
        var normalizedRunLabel = string.IsNullOrWhiteSpace(runLabel) ? "recent" : runLabel.Trim();

        _logger.LogInformation(
            "Starting {RunLabel} score updating process for the last {LookbackDays} day(s).",
            normalizedRunLabel,
            normalizedLookbackDays);
        try
        {
            // Score scraping is non-blocking
            try
            {
                var scores = await _webScraperService.ScrapeMatchScoresAsync();
                _logger.LogInformation("Scraped {Count} match scores from primary source.", scores.Count);
                await SaveMatchScores(scores);
            }
            catch (Exception scoreEx)
            {
                _logger.LogWarning(scoreEx, "❌ Primary score scraping failed.");
            }

            // Secondary score source (AiScore)
            try
            {
                var aiScores = await _webScraperService.ScrapeAiScoreMatchScoresAsync();
                await SaveAiScoreMatchScores(aiScores);
                var aiScoreSnapshot = _aiScoreSourceHealthTracker.GetSnapshot();
                _logger.LogInformation(
                    "AiScore stage finished with status {Status} at stage {Stage}. SofaScore stage will run for unresolved fixtures.",
                    aiScoreSnapshot.Status,
                    aiScoreSnapshot.LastStage ?? "unknown");
            }
            catch (Exception aiScoreEx)
            {
                _logger.LogWarning(aiScoreEx, "❌ AiScore scraping failed.");
            }

            await UpdatePredictionsWithActualResults(normalizedLookbackDays, normalizedRunLabel);
            _logger.LogInformation(
                "✅ Predictions updated with actual results for the {RunLabel} window.",
                normalizedRunLabel);

            await LogScrapingStatus(
                GetScoreUpdateEventName(normalizedRunLabel),
                "Success",
                $"✅ {normalizedRunLabel} score updating completed successfully for the last {normalizedLookbackDays} day(s).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during score updating.");
            await LogScrapingStatus(
                GetScoreUpdateEventName(normalizedRunLabel),
                "Failed",
                $"Score Update Error: {ex.Message}");
            throw;
        }
        finally
        {
            await PersistSourceRuntimeHealthSafelyAsync();
        }
    }
    private static string GetScoreUpdateEventName(string runLabel) =>
        ScrapingEventNames.ScoreUpdate(runLabel);

    private async Task PersistSourceRuntimeHealthSafelyAsync()
    {
        try
        {
            var aiScoreSnapshot = _aiScoreSourceHealthTracker.GetSnapshot();
            if (HasMeaningfulRuntimeSnapshot(aiScoreSnapshot.Status, aiScoreSnapshot.LastAttemptUtc, aiScoreSnapshot.LastSuccessUtc))
            {
                await PersistSourceRuntimeHealthAsync(AiScoreRuntimeEventName, aiScoreSnapshot.Status, aiScoreSnapshot);
            }

            var sofaScoreSnapshot = _sofaScoreSourceHealthTracker.GetSnapshot();
            if (HasMeaningfulRuntimeSnapshot(sofaScoreSnapshot.Status, sofaScoreSnapshot.LastAttemptUtc, sofaScoreSnapshot.LastSuccessUtc))
            {
                await PersistSourceRuntimeHealthAsync(SofaScoreRuntimeEventName, sofaScoreSnapshot.Status, sofaScoreSnapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist shared source runtime health snapshot.");
        }
    }

    private async Task PersistSourceRuntimeHealthAsync<TSnapshot>(string eventName, string status, TSnapshot snapshot)
    {
        var log = new ScrapingLog
        {
            EventName = eventName,
            Timestamp = DateTime.UtcNow,
            Status = string.IsNullOrWhiteSpace(status) ? "Idle" : status,
            Message = JsonSerializer.Serialize(snapshot)
        };

        await _dbContext.ScrapingLogs.AddAsync(log);
        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsSofaScoreIndexRowTooLarge(ex))
        {
            _logger.LogWarning(
                ex,
                "Skipping SofaScore score persistence for oversized team-name index entries on IX_SofaScoreMatchScores_MatchTime_HomeTeam_AwayTeam. Settlement continues, but add a migration to replace this index with a hash/shortened key index.");
        }
    }

    private static bool HasMeaningfulRuntimeSnapshot(string? status, DateTime? lastAttemptUtc, DateTime? lastSuccessUtc)
    {
        return lastAttemptUtc.HasValue ||
               lastSuccessUtc.HasValue ||
               !string.Equals(status, "Idle", StringComparison.OrdinalIgnoreCase);
    }

    private async Task UpdatePredictionsWithActualResults(int lookbackDays, string runLabel)
    {
        var nowLocal = DateTimeProvider.GetLocalTime();
        var nowUtc = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(nowLocal);
        var earliestSettlementDate = today.AddDays(-lookbackDays);
        var settlementDates = Enumerable.Range(0, lookbackDays + 1)
            .Select(offset => earliestSettlementDate.AddDays(offset))
            .ToHashSet();

        var startOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(earliestSettlementDate.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified));
        var endOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(today.AddDays(1).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified));

        var predictionsForSettlement = await _dbContext.Predictions
            .Where(p => settlementDates.Contains(p.MatchLocalDate) && p.IsCurrentRevision)
            .ToListAsync();
        var forecastsForSettlement = await _dbContext.ForecastObservations
            .Where(f => settlementDates.Contains(f.MatchLocalDate) && f.IsCurrentRevision)
            .ToListAsync();

        var settlementFixtures = BuildSettlementFixtureGroups(predictionsForSettlement, forecastsForSettlement);
        var eligibleSettlementFixtures = settlementFixtures
            .Where(fixture => IsFixtureEligibleForSettlement(fixture, today, nowUtc))
            .ToList();
        var futureSettlementFixtures = settlementFixtures
            .Where(fixture => !IsFixtureEligibleForSettlement(fixture, today, nowUtc))
            .ToList();

        foreach (var fixture in futureSettlementFixtures)
        {
            ClearFutureFixtureSettlement(fixture);
        }

        var eligiblePredictionsForSettlement = eligibleSettlementFixtures
            .SelectMany(fixture => fixture.Predictions)
            .ToList();
        var eligibleForecastsForSettlement = eligibleSettlementFixtures
            .SelectMany(fixture => fixture.Forecasts)
            .ToList();

        foreach (var prediction in eligiblePredictionsForSettlement)
        {
            RepairPredictionOutcomeFromStoredScore(prediction);
        }

        var sourceQualityLookup = await LoadSourceQualityLookupAsync();

        // ── Primary: FlashScore (faster final-status updates) ──
        var scores = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= startOfWindowUtc && s.MatchTime < endOfWindowUtc)
            .ToListAsync();

        var consolidatedFlashScores = ConsolidateFixtureSnapshots(
            scores,
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.League,
            score => score.MatchTime,
            score => score.IsLive);

        if (consolidatedFlashScores.Count > 0)
        {
            var flashScoreIndex = new FixtureCandidateIndex<MatchScore>(
                consolidatedFlashScores,
                score => score.HomeTeam,
                score => score.AwayTeam,
                score => score.League,
                score => score.MatchTime);
            var settlementFixtureIndex = new FixtureCandidateIndex<SettlementFixtureGroup>(
                settlementFixtures,
                fixture => fixture.HomeTeam,
                fixture => fixture.AwayTeam,
                fixture => fixture.League,
                fixture => fixture.ScheduledMatchTimeUtc);

            _logger.LogInformation(
                "Matching scores from FlashScore ({CandidateCount} consolidated from {RawCount} rows) against {FixtureCount} fixtures ({PredCount} predictions, {ForecastCount} forecasts) in the {LookbackDays}-day settlement window.",
                consolidatedFlashScores.Count,
                scores.Count,
                eligibleSettlementFixtures.Count,
                eligiblePredictionsForSettlement.Count,
                eligibleForecastsForSettlement.Count,
                lookbackDays);

            var flashMatchedFixtures = 0;
            for (var index = 0; index < eligibleSettlementFixtures.Count; index++)
            {
                var fixture = eligibleSettlementFixtures[index];
                var flashMatch = FindBestFixtureCandidate(
                    flashScoreIndex,
                    fixture.HomeTeam,
                    fixture.AwayTeam,
                    fixture.League,
                    fixture.Date,
                    fixture.ScheduledMatchTimeUtc,
                    score => score.HomeTeam,
                    score => score.AwayTeam,
                    score => score.League,
                    score => score.MatchTime,
                    score => score.IsLive,
                    score => GetSourceQualityReliability(sourceQualityLookup, "FlashScore", score.League, score.MatchTime));

                if (flashMatch != null &&
                    IsReciprocalFixtureMatch(
                        settlementFixtureIndex,
                        fixture,
                        flashMatch,
                        score => score.HomeTeam,
                        score => score.AwayTeam,
                        score => score.League,
                        score => score.MatchTime,
                        score => score.IsLive))
                {
                    ApplyFixtureSettlement(fixture, flashMatch.Score, flashMatch.BTTSLabel, flashMatch.IsLive);
                    flashMatchedFixtures++;
                }

                LogFixtureMatchingProgress("FlashScore", index + 1, eligibleSettlementFixtures.Count, flashMatchedFixtures);
            }
        }

        // ── Fallback: AiScore for any fixtures still missing a score or still marked live ──
        var aiScores = await _dbContext.AiScoreMatchScores
            .Where(s => s.MatchTime >= startOfWindowUtc && s.MatchTime < endOfWindowUtc)
            .ToListAsync();

        var consolidatedAiScores = ConsolidateFixtureSnapshots(
            aiScores,
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.League,
            score => score.MatchTime,
            score => score.IsLive);

        var incompleteFixtures = eligibleSettlementFixtures
            .Where(NeedsFixtureSettlementRepair)
            .ToList();
        var incompletePredictions = incompleteFixtures
            .SelectMany(fixture => fixture.Predictions)
            .Where(NeedsPredictionSettlementRepair)
            .ToList();
        var incompleteForecasts = incompleteFixtures
            .SelectMany(fixture => fixture.Forecasts)
            .Where(NeedsForecastSettlementRepair)
            .ToList();

        if (incompleteFixtures.Count > 0 && consolidatedAiScores.Count > 0)
        {
            var aiScoreIndex = new FixtureCandidateIndex<AiScoreMatchScore>(
                consolidatedAiScores,
                score => score.HomeTeam,
                score => score.AwayTeam,
                score => score.League,
                score => score.MatchTime);
            var incompleteFixtureIndex = new FixtureCandidateIndex<SettlementFixtureGroup>(
                incompleteFixtures,
                fixture => fixture.HomeTeam,
                fixture => fixture.AwayTeam,
                fixture => fixture.League,
                fixture => fixture.ScheduledMatchTimeUtc);

            _logger.LogInformation(
                "Attempting fallback score match from AiScore for {FixtureCount} incomplete fixtures using {CandidateCount} consolidated rows ({RawCount} raw rows).",
                incompleteFixtures.Count,
                consolidatedAiScores.Count,
                aiScores.Count);

            var aiMatchedFixtures = 0;
            for (var index = 0; index < incompleteFixtures.Count; index++)
            {
                var fixture = incompleteFixtures[index];
                var aiMatch = FindBestFixtureCandidate(
                    aiScoreIndex,
                    fixture.HomeTeam,
                    fixture.AwayTeam,
                    fixture.League,
                    fixture.Date,
                    fixture.ScheduledMatchTimeUtc,
                    score => score.HomeTeam,
                    score => score.AwayTeam,
                    score => score.League,
                    score => score.MatchTime,
                    score => score.IsLive,
                    score => GetSourceQualityReliability(sourceQualityLookup, "AiScore", score.League, score.MatchTime));

                if (aiMatch != null &&
                    IsReciprocalFixtureMatch(
                        incompleteFixtureIndex,
                        fixture,
                        aiMatch,
                        score => score.HomeTeam,
                        score => score.AwayTeam,
                        score => score.League,
                        score => score.MatchTime,
                        score => score.IsLive))
                {
                    ApplyFixtureSettlement(fixture, aiMatch.Score, aiMatch.BTTSLabel, aiMatch.IsLive);
                    aiMatchedFixtures++;
                }

                LogFixtureMatchingProgress("AiScore", index + 1, incompleteFixtures.Count, aiMatchedFixtures);
            }
        }

        incompleteFixtures = eligibleSettlementFixtures
            .Where(NeedsFixtureSettlementRepair)
            .ToList();

        if (incompleteFixtures.Count > 0)
        {
            var sofaScoreRequests = incompleteFixtures
                .Select(BuildSofaScoreFixtureRequest)
                .ToList();
            var sofaScores = await _webScraperService.ScrapeSofaScoreMatchScoresAsync(sofaScoreRequests);
            await SaveSofaScoreMatchScores(sofaScores);

            if (sofaScores.Count > 0)
            {
                var sofaScoreIndex = new FixtureCandidateIndex<SofaScoreMatchScore>(
                    sofaScores,
                    score => score.HomeTeam,
                    score => score.AwayTeam,
                    score => score.League,
                    score => score.MatchTime);
                var incompleteFixtureIndex = new FixtureCandidateIndex<SettlementFixtureGroup>(
                    incompleteFixtures,
                    fixture => fixture.HomeTeam,
                    fixture => fixture.AwayTeam,
                    fixture => fixture.League,
                    fixture => fixture.ScheduledMatchTimeUtc);

                _logger.LogInformation(
                    "Attempting targeted fallback score match from SofaScore for {FixtureCount} incomplete fixtures using {CandidateCount} targeted row(s).",
                    incompleteFixtures.Count,
                    sofaScores.Count);

                var sofaMatchedFixtures = 0;
                for (var index = 0; index < incompleteFixtures.Count; index++)
                {
                    var fixture = incompleteFixtures[index];
                    var sofaMatch = FindBestFixtureCandidate(
                        sofaScoreIndex,
                        fixture.HomeTeam,
                        fixture.AwayTeam,
                        fixture.League,
                        fixture.Date,
                        fixture.ScheduledMatchTimeUtc,
                        score => score.HomeTeam,
                        score => score.AwayTeam,
                        score => score.League,
                        score => score.MatchTime,
                        score => score.IsLive,
                        score => GetSourceQualityReliability(sourceQualityLookup, "SofaScore", score.League, score.MatchTime));

                    if (sofaMatch != null &&
                        IsReciprocalFixtureMatch(
                            incompleteFixtureIndex,
                            fixture,
                            sofaMatch,
                            score => score.HomeTeam,
                            score => score.AwayTeam,
                            score => score.League,
                            score => score.MatchTime,
                            score => score.IsLive))
                    {
                        ApplyFixtureSettlement(fixture, sofaMatch.Score, sofaMatch.BTTSLabel, sofaMatch.IsLive);
                        sofaMatchedFixtures++;
                    }

                    LogFixtureMatchingProgress("SofaScore", index + 1, incompleteFixtures.Count, sofaMatchedFixtures);
                }
            }
        }

        ApplyExactFinishedSourceRepairs(eligibleSettlementFixtures, consolidatedFlashScores, consolidatedAiScores, sourceQualityLookup);
        ApplyExactLiveSourceReopens(eligibleSettlementFixtures, consolidatedFlashScores, consolidatedAiScores, sourceQualityLookup);

        // ── Matching Statistics & Diagnostics ──
        var matchedCount = eligiblePredictionsForSettlement.Count(p => !string.IsNullOrEmpty(p.ActualScore));
        var unmatchedPredictions = eligiblePredictionsForSettlement
            .Where(p => string.IsNullOrEmpty(p.ActualScore))
            .ToList();

        _logger.LogInformation(
            "📊 Score matching summary: {Matched}/{Total} predictions matched ({Percentage}%) in the {LookbackDays}-day settlement window, {Unmatched} unmatched.",
            matchedCount,
            eligiblePredictionsForSettlement.Count,
            eligiblePredictionsForSettlement.Count > 0 ? (matchedCount * 100 / eligiblePredictionsForSettlement.Count) : 0,
            lookbackDays,
            unmatchedPredictions.Count);

        if (unmatchedPredictions.Count > 0)
        {
            var topUnmatched = unmatchedPredictions.Take(15);
            foreach (var p in topUnmatched)
            {
                _logger.LogWarning(
                    "⚠️ Unmatched prediction: [{Category}] {Home} vs {Away} ({League}, {Time})",
                    p.PredictionCategory, p.HomeTeam, p.AwayTeam, p.League, p.Time);
            }

            if (unmatchedPredictions.Count > 15)
            {
                _logger.LogWarning("⚠️ ...and {More} more unmatched predictions.",
                    unmatchedPredictions.Count - 15);
            }
        }

        _logger.LogInformation(
            "✅ Predictions updated successfully for the {RunLabel} window.",
            runLabel);

        await _dbContext.SaveChangesAsync();
    }

    private static List<SettlementFixtureGroup> BuildSettlementFixtureGroups(
        IEnumerable<Prediction> predictions,
        IEnumerable<ForecastObservation> forecasts)
    {
        var fixtures = new Dictionary<(DateOnly Date, string FixtureKey, long MatchTimeTicks), SettlementFixtureGroup>();

        foreach (var prediction in predictions)
        {
            var localDate = prediction.MatchLocalDate != default
                ? prediction.MatchLocalDate
                : DateTimeProvider.ParseLocalDateOrNull(prediction.Date) ?? DateOnly.MinValue;
            var scheduledMatchTime = ResolveScheduledMatchTime(localDate, prediction.MatchLocalTime, prediction.MatchDateTime);
            var canonicalFixtureKey = string.IsNullOrWhiteSpace(prediction.FixtureKey)
                ? FixtureIdentityFactory.FromPrediction(prediction).FixtureKey
                : prediction.FixtureKey;
            var groupKey = (
                localDate,
                canonicalFixtureKey,
                scheduledMatchTime?.Ticks ?? 0L);

            if (!fixtures.TryGetValue(groupKey, out var fixture))
            {
                fixture = new SettlementFixtureGroup
                {
                    MatchLocalDate = localDate,
                    Date = localDate == DateOnly.MinValue ? prediction.Date ?? string.Empty : DateTimeProvider.FormatLocalDate(localDate),
                    HomeTeam = prediction.HomeTeam ?? string.Empty,
                    AwayTeam = prediction.AwayTeam ?? string.Empty,
                    League = prediction.League ?? string.Empty,
                    FixtureKey = groupKey.Item2,
                    ScheduledMatchTimeUtc = scheduledMatchTime
                };
                fixtures[groupKey] = fixture;
            }

            fixture.Predictions.Add(prediction);
        }

        foreach (var forecast in forecasts)
        {
            var localDate = forecast.MatchLocalDate != default
                ? forecast.MatchLocalDate
                : DateTimeProvider.ParseLocalDateOrNull(forecast.Date) ?? DateOnly.MinValue;
            var scheduledMatchTime = ResolveScheduledMatchTime(localDate, forecast.MatchLocalTime, forecast.MatchDateTime);
            var forecastFixtureKey = string.IsNullOrWhiteSpace(forecast.FixtureKey)
                ? FixtureIdentityFactory.FromForecast(forecast).FixtureKey
                : forecast.FixtureKey;
            var fixtureKey = (
                localDate,
                forecastFixtureKey,
                scheduledMatchTime?.Ticks ?? 0L);

            if (!fixtures.TryGetValue(fixtureKey, out var fixture))
            {
                fixture = new SettlementFixtureGroup
                {
                    MatchLocalDate = localDate,
                    Date = localDate == DateOnly.MinValue ? forecast.Date ?? string.Empty : DateTimeProvider.FormatLocalDate(localDate),
                    HomeTeam = forecast.HomeTeam ?? string.Empty,
                    AwayTeam = forecast.AwayTeam ?? string.Empty,
                    League = forecast.League ?? string.Empty,
                    FixtureKey = fixtureKey.Item2,
                    ScheduledMatchTimeUtc = scheduledMatchTime
                };
                fixtures[fixtureKey] = fixture;
            }

            fixture.Forecasts.Add(forecast);
        }

        return fixtures.Values
            .OrderBy(fixture => fixture.ScheduledMatchTimeUtc)
            .ThenBy(fixture => fixture.HomeTeam)
            .ThenBy(fixture => fixture.AwayTeam)
            .ToList();
    }

    private void ApplyFixtureSettlement(SettlementFixtureGroup fixture, string score, bool bttsLabel, bool isLive)
    {
        foreach (var prediction in fixture.Predictions)
        {
            UpdatePredictionSettlementState(prediction, score, bttsLabel, isLive);
        }

        foreach (var forecast in fixture.Forecasts)
        {
            UpdateForecastObservationState(forecast, score, bttsLabel, isLive);
        }
    }

    private static bool IsFixtureEligibleForSettlement(SettlementFixtureGroup fixture, DateOnly today, DateTime nowUtc)
    {
        if (fixture.ScheduledMatchTimeUtc.HasValue)
        {
            return fixture.ScheduledMatchTimeUtc.Value <= nowUtc + FutureFixtureSettlementTolerance;
        }

        if (fixture.MatchLocalDate != default)
        {
            return fixture.MatchLocalDate < today;
        }

        var parsedDate = DateTimeProvider.ParseLocalDateOrNull(fixture.Date);
        return parsedDate.HasValue && parsedDate.Value < today;
    }

    private static void ClearFutureFixtureSettlement(SettlementFixtureGroup fixture)
    {
        foreach (var prediction in fixture.Predictions)
        {
            prediction.ActualScore = null;
            prediction.ActualOutcome = null;
            prediction.IsLive = false;
        }

        foreach (var forecast in fixture.Forecasts)
        {
            forecast.ActualScore = null;
            forecast.ActualOutcome = null;
            forecast.OutcomeOccurred = null;
            forecast.IsSettled = false;
            forecast.IsLive = false;
            forecast.SettledAt = null;
        }
    }

    private static bool NeedsFixtureSettlementRepair(SettlementFixtureGroup fixture)
    {
        return fixture.Predictions.Any(NeedsPredictionSettlementRepair) ||
               fixture.Forecasts.Any(NeedsForecastSettlementRepair);
    }

    private static bool NeedsForecastSettlementRepair(ForecastObservation forecast)
    {
        return string.IsNullOrWhiteSpace(forecast.ActualScore) || forecast.IsLive || !forecast.IsSettled;
    }

    private static SofaScoreFixtureRequest BuildSofaScoreFixtureRequest(SettlementFixtureGroup fixture)
    {
        return new SofaScoreFixtureRequest
        {
            League = fixture.League,
            HomeTeam = fixture.HomeTeam,
            AwayTeam = fixture.AwayTeam,
            MatchLocalDate = fixture.MatchLocalDate,
            ScheduledMatchTimeUtc = fixture.ScheduledMatchTimeUtc,
            FixtureKey = fixture.FixtureKey
        };
    }

    private void ApplyExactFinishedSourceRepairs(
        IEnumerable<SettlementFixtureGroup> fixtures,
        IReadOnlyList<MatchScore> flashScores,
        IReadOnlyList<AiScoreMatchScore> aiScores,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        var flashIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);

        var aiIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);

        foreach (var fixture in fixtures)
        {
            var flashResolved = FindExactFinishedSourceCandidate(
                fixture,
                flashIndex,
                score => score.MatchTime,
                score => score.League,
                score => score.Score,
                score => GetSourceQualityReliability(sourceQualityLookup, "FlashScore", score.League, score.MatchTime));

            var aiResolved = FindExactFinishedSourceCandidate(
                fixture,
                aiIndex,
                score => score.MatchTime,
                score => score.League,
                score => score.Score,
                score => GetSourceQualityReliability(sourceQualityLookup, "AiScore", score.League, score.MatchTime));

            object? resolved = ChooseBestExactSourceCandidate(
                fixture,
                flashResolved,
                aiResolved,
                sourceQualityLookup);

            if (resolved is null)
            {
                continue;
            }

            var score = resolved switch
            {
                MatchScore flashScore => flashScore.Score,
                AiScoreMatchScore aiScore => aiScore.Score,
                _ => string.Empty
            };

            var bttsLabel = resolved switch
            {
                MatchScore flashScore => flashScore.BTTSLabel,
                AiScoreMatchScore aiScore => aiScore.BTTSLabel,
                _ => false
            };

            if (string.IsNullOrWhiteSpace(score))
            {
                continue;
            }

            ApplyFixtureSettlement(fixture, score, bttsLabel, false);
        }
    }

    private void ApplyExactLiveSourceReopens(
        IEnumerable<SettlementFixtureGroup> fixtures,
        IReadOnlyList<MatchScore> flashScores,
        IReadOnlyList<AiScoreMatchScore> aiScores,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        var flashFinishedIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var aiFinishedIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var flashLiveIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var aiLiveIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);

        foreach (var fixture in fixtures)
        {
            var hasFinishedSource =
                FindExactFinishedSourceCandidate(
                    fixture,
                    flashFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score,
                    score => GetSourceQualityReliability(sourceQualityLookup, "FlashScore", score.League, score.MatchTime)) is not null ||
                FindExactFinishedSourceCandidate(
                    fixture,
                    aiFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score,
                    score => GetSourceQualityReliability(sourceQualityLookup, "AiScore", score.League, score.MatchTime)) is not null;

            if (hasFinishedSource)
            {
                continue;
            }

            var flashResolved = FindLatestExactLiveSourceCandidate(
                fixture,
                flashLiveIndex,
                score => score.MatchTime,
                score => GetSourceQualityReliability(sourceQualityLookup, "FlashScore", score.League, score.MatchTime));

            var aiResolved = FindLatestExactLiveSourceCandidate(
                fixture,
                aiLiveIndex,
                score => score.MatchTime,
                score => GetSourceQualityReliability(sourceQualityLookup, "AiScore", score.League, score.MatchTime));

            object? resolved = ChooseBestLiveSourceCandidate(
                flashResolved,
                aiResolved,
                sourceQualityLookup);

            if (resolved is null)
            {
                continue;
            }

            var score = resolved switch
            {
                MatchScore flashScore => flashScore.Score,
                AiScoreMatchScore aiScore => aiScore.Score,
                _ => string.Empty
            };

            var bttsLabel = resolved switch
            {
                MatchScore flashScore => flashScore.BTTSLabel,
                AiScoreMatchScore aiScore => aiScore.BTTSLabel,
                _ => false
            };

            if (string.IsNullOrWhiteSpace(score))
            {
                continue;
            }

            ApplyFixtureSettlement(fixture, score, bttsLabel, true);
        }
    }

    private void LogFixtureMatchingProgress(string sourceName, int processed, int total, int matchedFixtures)
    {
        if (total < 250)
        {
            return;
        }

        if (processed % 250 != 0 && processed != total)
        {
            return;
        }

        _logger.LogInformation(
            "{SourceName} score matching progress: {Processed}/{Total} fixtures processed, {Matched} matched so far.",
            sourceName,
            processed,
            total,
            matchedFixtures);
    }

    private static bool IsReciprocalFixtureMatch<TCandidate>(
        FixtureCandidateIndex<SettlementFixtureGroup> fixtureIndex,
        SettlementFixtureGroup expectedFixture,
        TCandidate candidate,
        Func<TCandidate, string> homeSelector,
        Func<TCandidate, string> awaySelector,
        Func<TCandidate, string?> leagueSelector,
        Func<TCandidate, DateTime?> matchTimeSelector,
        Func<TCandidate, bool> isLiveSelector)
        where TCandidate : class
    {
        var candidateDate = matchTimeSelector(candidate).HasValue
            ? DateTimeProvider.ConvertUtcToLocal(matchTimeSelector(candidate)!.Value).ToString("dd-MM-yyyy")
            : expectedFixture.Date;

        var resolvedFixture = FindBestFixtureCandidate(
            fixtureIndex,
            homeSelector(candidate),
            awaySelector(candidate),
            leagueSelector(candidate),
            candidateDate,
            matchTimeSelector(candidate),
            fixture => fixture.HomeTeam,
            fixture => fixture.AwayTeam,
            fixture => fixture.League,
            fixture => fixture.ScheduledMatchTimeUtc,
            _ => false);

        return ReferenceEquals(resolvedFixture, expectedFixture);
    }

    private static Dictionary<(string Date, string HomeKey, string AwayKey), List<T>> BuildExactFinishedCandidateIndex<T>(
        IEnumerable<T> candidates,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, string?> leagueSelector)
        where T : class
    {
        return candidates
            .Where(candidate => matchTimeSelector(candidate).HasValue)
            .GroupBy(candidate =>
            {
                var matchTime = matchTimeSelector(candidate)!.Value;
                var date = DateTimeProvider.ConvertUtcToLocal(matchTime).ToString("dd-MM-yyyy");
                return (
                    Date: date,
                    HomeKey: ScoreMatchingHelper.CreateTeamLookupKey(homeSelector(candidate)),
                    AwayKey: ScoreMatchingHelper.CreateTeamLookupKey(awaySelector(candidate)));
            })
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static T? FindExactFinishedSourceCandidate<T>(
        SettlementFixtureGroup fixture,
        IReadOnlyDictionary<(string Date, string HomeKey, string AwayKey), List<T>> candidateIndex,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, string?> leagueSelector,
        Func<T, string?> scoreSelector,
        Func<T, double>? qualityScoreSelector = null)
        where T : class
    {
        var key = (
            Date: fixture.Date,
            HomeKey: ScoreMatchingHelper.CreateTeamLookupKey(fixture.HomeTeam),
            AwayKey: ScoreMatchingHelper.CreateTeamLookupKey(fixture.AwayTeam));

        if (!candidateIndex.TryGetValue(key, out var candidates) || candidates.Count == 0)
        {
            return default;
        }

        if (fixture.ScheduledMatchTimeUtc is null)
        {
            return ResolveExtendedExactFinishedCandidate(
                candidates
                    .Select(candidate => new RankedExactFinishedCandidate<T>(
                        candidate,
                        matchTimeSelector(candidate),
                        0d,
                        ScoreMatchingHelper.GetLeagueMatchScore(fixture.League, leagueSelector(candidate)),
                        scoreSelector(candidate),
                        qualityScoreSelector?.Invoke(candidate) ?? 0.5))
                    .OrderByDescending(candidate => candidate.MatchTime ?? DateTime.MinValue)
                    .ToList());
        }

        var ranked = candidates
            .Select(candidate => new RankedExactFinishedCandidate<T>(
                candidate,
                matchTimeSelector(candidate),
                matchTimeSelector(candidate).HasValue
                    ? Math.Abs((matchTimeSelector(candidate)!.Value - fixture.ScheduledMatchTimeUtc.Value).TotalMinutes)
                    : double.MaxValue,
                ScoreMatchingHelper.GetLeagueMatchScore(fixture.League, leagueSelector(candidate)),
                scoreSelector(candidate),
                qualityScoreSelector?.Invoke(candidate) ?? 0.5))
            .OrderBy(candidate => candidate.MinutesApart)
            .ThenByDescending(candidate => candidate.QualityScore)
            .ThenByDescending(candidate => candidate.LeagueScore)
            .ThenByDescending(candidate => candidate.MatchTime ?? DateTime.MinValue)
            .ToList();

        return ResolveExtendedExactFinishedCandidate(ranked);
    }

    private static T? FindLatestExactLiveSourceCandidate<T>(
        SettlementFixtureGroup fixture,
        IReadOnlyDictionary<(string Date, string HomeKey, string AwayKey), List<T>> candidateIndex,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, double>? qualityScoreSelector = null)
        where T : class
    {
        var key = (
            Date: fixture.Date,
            HomeKey: ScoreMatchingHelper.CreateTeamLookupKey(fixture.HomeTeam),
            AwayKey: ScoreMatchingHelper.CreateTeamLookupKey(fixture.AwayTeam));

        if (!candidateIndex.TryGetValue(key, out var candidates) || candidates.Count == 0)
        {
            return default;
        }

        return candidates
            .OrderByDescending(candidate => qualityScoreSelector?.Invoke(candidate) ?? 0.5)
            .ThenByDescending(candidate => matchTimeSelector(candidate) ?? DateTime.MinValue)
            .FirstOrDefault();
    }
    
    private static bool TryParseScore(string score, out int home, out int away)
    {
        home = away = 0;
        if (string.IsNullOrWhiteSpace(score)) return false;

        // supports "1:0", "1 - 0", "1–0", "1—0", and spaces
        var normalized = score.Replace("–", "-").Replace("—", "-").Trim();

        var parts = normalized.Contains(':')
            ? normalized.Split(':', StringSplitOptions.TrimEntries)
            : normalized.Split('-', StringSplitOptions.TrimEntries);

        if (parts.Length != 2) return false;

        return int.TryParse(parts[0].Trim(), out home) && int.TryParse(parts[1].Trim(), out away);
    }

    private string DetermineDrawOutcome(string score)
    {
        return TryParseScore(score, out var h, out var a)
            ? h == a ? "Draw" : "Not Draw"
            : "Unknown";
    }

    private string DetermineOver25Outcome(string score)
    {
        return TryParseScore(score, out var h, out var a)
            ? h + a > 2 ? "Over 2.5" : "Under 2.5"
            : "Unknown";
    }

    private string DetermineStraightWinOutcome(string score)
    {
        if (!TryParseScore(score, out var h, out var a)) return "Unknown";
        if (h > a) return "Home Win";
        return h < a ? "Away Win" : "Draw";
    }

    private void UpdatePredictionSettlementState(Prediction prediction, string score, bool bttsLabel, bool isLive)
    {
        var effectiveIsLive = DetermineEffectivePredictionIsLive(prediction, score, bttsLabel, isLive);

        prediction.ActualScore = score;
        prediction.IsLive = effectiveIsLive;

        if (effectiveIsLive)
        {
            prediction.ActualOutcome = null;
            return;
        }

        prediction.ActualOutcome = DeterminePredictionActualOutcome(
            prediction.PredictionCategory,
            score,
            bttsLabel);
    }

    private void RepairPredictionOutcomeFromStoredScore(Prediction prediction)
    {
        if (prediction.IsLive || string.IsNullOrWhiteSpace(prediction.ActualScore) ||
            !IsOutcomeMissing(prediction.ActualOutcome))
        {
            return;
        }

        prediction.ActualOutcome = DeterminePredictionActualOutcome(
            prediction.PredictionCategory,
            prediction.ActualScore,
            null);
    }

    private static bool NeedsPredictionSettlementRepair(Prediction prediction)
    {
        return string.IsNullOrWhiteSpace(prediction.ActualScore) ||
               DetermineEffectiveIsLive(prediction.IsLive) ||
               (!string.IsNullOrWhiteSpace(prediction.ActualScore) && IsOutcomeMissing(prediction.ActualOutcome));
    }

    private string? DeterminePredictionActualOutcome(string predictionCategory, string score, bool? bttsLabel)
    {
        return predictionCategory switch
        {
            "BothTeamsScore" => DetermineBttsOutcome(score, bttsLabel),
            "Draw" => DetermineDrawOutcome(score),
            "Over2.5Goals" => DetermineOver25Outcome(score),
            "Under2.5Goals" => DetermineOver25Outcome(score),
            "StraightWin" => DetermineStraightWinOutcome(score),
            _ => null
        };
    }

    private string DetermineBttsOutcome(string score, bool? fallbackBttsLabel)
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

    private static bool IsOutcomeMissing(string? actualOutcome)
    {
        return string.IsNullOrWhiteSpace(actualOutcome) ||
               string.Equals(actualOutcome, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetermineEffectiveIsLive(bool sourceIsLive)
    {
        return sourceIsLive;
    }

    private void UpdateForecastObservationState(ForecastObservation forecast, string score, bool bttsLabel, bool isLive)
    {
        var effectiveIsLive = DetermineEffectiveForecastIsLive(forecast, score, bttsLabel, isLive);

        forecast.ActualScore = score;
        forecast.IsLive = effectiveIsLive;

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

    private bool? DetermineForecastOutcomeOccurred(PredictionMarket market, string score, bool bttsLabel)
    {
        switch (market)
        {
            case PredictionMarket.BothTeamsScore:
                if (TryParseScore(score, out var homeGoals, out var awayGoals))
                    return homeGoals > 0 && awayGoals > 0;

                return bttsLabel;

            case PredictionMarket.Over25Goals:
                return TryParseScore(score, out var homeOver, out var awayOver)
                    ? homeOver + awayOver > 2
                    : null;

            case PredictionMarket.Under25Goals:
                return TryParseScore(score, out var homeUnder, out var awayUnder)
                    ? homeUnder + awayUnder <= 2
                    : null;

            case PredictionMarket.Draw:
                return TryParseScore(score, out var homeDraw, out var awayDraw)
                    ? homeDraw == awayDraw
                    : null;

            case PredictionMarket.HomeWin:
                return TryParseScore(score, out var homeWin, out var awayWin)
                    ? homeWin > awayWin
                    : null;

            case PredictionMarket.AwayWin:
                return TryParseScore(score, out var homeAway, out var awayAway)
                    ? awayAway > homeAway
                    : null;

            case PredictionMarket.StraightWin:
                return DetermineStraightWinOutcome(score) == "Home Win" || DetermineStraightWinOutcome(score) == "Away Win";

            default:
                return null;
        }
    }

    private string? DetermineForecastActualOutcome(PredictionMarket market, string score, bool bttsLabel)
    {
        return market switch
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
    }

    private bool DetermineEffectivePredictionIsLive(Prediction prediction, string score, bool? bttsLabel, bool sourceIsLive)
    {
        if (!sourceIsLive)
        {
            return false;
        }

        return PredictionMarketExtensions.TryFromCategory(prediction.PredictionCategory, out var market) &&
               CanSettleMarketEarly(market, score, bttsLabel)
            ? false
            : DetermineEffectiveIsLive(sourceIsLive);
    }

    private bool DetermineEffectiveForecastIsLive(ForecastObservation forecast, string score, bool? bttsLabel, bool sourceIsLive)
    {
        if (!sourceIsLive)
        {
            return false;
        }

        return CanSettleMarketEarly(forecast.Market, score, bttsLabel)
            ? false
            : DetermineEffectiveIsLive(sourceIsLive);
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

    private static bool HasOver25BeenMet(string score)
    {
        return TryParseScore(score, out var homeGoals, out var awayGoals) &&
               homeGoals + awayGoals > 2;
    }

    
    private async Task SaveMatchScores(List<MatchScore> scores)
    {
        if (scores.Count == 0) return;

        var localDates = scores
            .Select(score => DateTimeProvider.ConvertUtcToLocal(score.MatchTime).Date)
            .Distinct()
            .ToList();
        var windowStartUtc = DateTimeProvider.ConvertLocalToUtc(localDates.Min());
        var windowEndUtc = DateTimeProvider.ConvertLocalToUtc(localDates.Max().AddDays(1));

        var existingScoresList = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= windowStartUtc && s.MatchTime < windowEndUtc)
            .ToListAsync();

        var existingScoresDict = existingScoresList
            .GroupBy(GetStoredScoreSnapshotKey)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var incomingScore in scores)
        {
            var key = GetStoredScoreSnapshotKey(incomingScore);

            if (existingScoresDict.TryGetValue(key, out var existingRecord))
            {
                existingRecord.MatchTime = ResolvePreferredStoredMatchTime(existingRecord.MatchTime, incomingScore.MatchTime, existingRecord.IsLive, incomingScore.IsLive);

                if (ShouldOverwriteStoredScore(existingRecord.Score, existingRecord.BTTSLabel, existingRecord.IsLive, existingRecord.MatchTime, incomingScore))
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                    existingRecord.BTTSLabel = incomingScore.BTTSLabel;
                }
            }
            else
            {
                _dbContext.MatchScores.Add(incomingScore);
                existingScoresDict[key] = incomingScore;
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task SaveAiScoreMatchScores(List<AiScoreMatchScore> scores)
    {
        if (scores.Count == 0) return;

        var minTime = scores.Min(s => s.MatchTime);
        var maxTime = scores.Max(s => s.MatchTime);

        var existingScoresList = await _dbContext.AiScoreMatchScores
            .Where(s => s.MatchTime >= minTime && s.MatchTime <= maxTime)
            .ToListAsync();

        var existingScoresDict = existingScoresList
            .GroupBy(s => (s.HomeTeam, s.AwayTeam, s.MatchTime))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var incomingScore in scores)
        {
            var key = (incomingScore.HomeTeam, incomingScore.AwayTeam, incomingScore.MatchTime);

            if (existingScoresDict.TryGetValue(key, out var existingRecord))
            {
                if (ShouldOverwriteStoredScore(existingRecord.Score, existingRecord.BTTSLabel, existingRecord.IsLive, existingRecord.MatchTime, incomingScore))
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                    existingRecord.BTTSLabel = incomingScore.BTTSLabel;
                }
            }
            else
            {
                _dbContext.AiScoreMatchScores.Add(incomingScore);
            }
        }

        await _dbContext.SaveChangesAsync();
    }
    private static string Norm(string? s) =>
        (s ?? "").Trim().ToLowerInvariant();

    private static (string Date, string Home, string Away, string League) CreateScoreFixtureKey(
        string? date,
        string? homeTeam,
        string? awayTeam,
        string? league)
    {
        return (
            date ?? string.Empty,
            ScoreMatchingHelper.CreateTeamLookupKey(homeTeam, league),
            ScoreMatchingHelper.CreateTeamLookupKey(awayTeam, league),
            ScoreMatchingHelper.CreateLeagueLookupKey(league));
    }

    private static DateTime? ResolveScheduledMatchTime(DateOnly? localDate, TimeOnly? localTime, DateTime? matchDateTime)
    {
        if (matchDateTime.HasValue)
        {
            return matchDateTime.Value;
        }

        if (!localDate.HasValue || !localTime.HasValue)
        {
            return null;
        }

        var localDateTime = localDate.Value.ToDateTime(localTime.Value);
        return DateTimeProvider.ConvertLocalToUtc(localDateTime);
    }

    private static double GetMatchTimeScore(DateTime? targetMatchTime, DateTime? candidateMatchTime)
    {
        if (!targetMatchTime.HasValue || !candidateMatchTime.HasValue)
        {
            return 0;
        }

        var minutesApart = Math.Abs((candidateMatchTime.Value - targetMatchTime.Value).TotalMinutes);
        if (minutesApart <= 10) return 1.0;
        if (minutesApart <= 45) return 0.6;
        if (minutesApart <= 120) return 0.25;
        return 0;
    }

    private static T? FindBestFixtureCandidate<T>(
        FixtureCandidateIndex<T> candidateIndex,
        string homeTeam,
        string awayTeam,
        string? league,
        string? targetDate,
        DateTime? targetMatchTime,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, string?> leagueSelector,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, bool> isLiveSelector,
        Func<T, double>? qualityScoreSelector = null)
        where T : class
    {
        var targetHomeKey = ScoreMatchingHelper.CreateTeamLookupKey(homeTeam, league);
        var targetAwayKey = ScoreMatchingHelper.CreateTeamLookupKey(awayTeam, league);

        var exactCandidates = candidateIndex.GetExactPairCandidates(targetHomeKey, targetAwayKey).ToList();
        var scopedExactCandidates = exactCandidates
            .Where(candidate => ExactCandidateMatchesTargetDate(candidate, targetDate, matchTimeSelector))
            .ToList();

        if (scopedExactCandidates.Count == 1)
        {
            return scopedExactCandidates[0];
        }

        if (scopedExactCandidates.Count > 1)
        {
            return scopedExactCandidates.All(isLiveSelector)
                ? scopedExactCandidates
                    .OrderByDescending(candidate => matchTimeSelector(candidate) ?? DateTime.MinValue)
                    .First()
                : SelectBestFixtureCandidate(
                    scopedExactCandidates,
                    homeTeam,
                    awayTeam,
                    league,
                    targetMatchTime,
                    homeSelector,
                    awaySelector,
                    leagueSelector,
                    matchTimeSelector,
                    isLiveSelector,
                    qualityScoreSelector);
        }

        if (exactCandidates.Count == 1 && string.IsNullOrWhiteSpace(targetDate))
        {
            return exactCandidates[0];
        }

        var scopedCandidates = candidateIndex.GetScopedCandidates(targetDate, league, targetMatchTime);

        return SelectBestFixtureCandidate(
            scopedCandidates.Count > 0 ? scopedCandidates : candidateIndex.AllCandidates,
            homeTeam,
            awayTeam,
            league,
            targetMatchTime,
            homeSelector,
            awaySelector,
            leagueSelector,
            matchTimeSelector,
            isLiveSelector,
            qualityScoreSelector);
    }

    private static T? SelectBestFixtureCandidate<T>(
        IEnumerable<T> candidates,
        string homeTeam,
        string awayTeam,
        string? league,
        DateTime? targetMatchTime,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, string?> leagueSelector,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, bool> isLiveSelector,
        Func<T, double>? qualityScoreSelector = null)
    {
        var scoredCandidates = new List<(T Candidate, double BaseScore, double TotalScore, bool ExactPair)>();

        foreach (var candidate in candidates)
        {
            var candidateLeague = leagueSelector(candidate);
            var homeMatch = ScoreMatchingHelper.GetTeamMatchResult(homeTeam, homeSelector(candidate), league, candidateLeague);
            var awayMatch = ScoreMatchingHelper.GetTeamMatchResult(awayTeam, awaySelector(candidate), league, candidateLeague);
            if (!homeMatch.IsMatch || !awayMatch.IsMatch)
            {
                continue;
            }

            var baseScore = (homeMatch.Score + awayMatch.Score) / 2.0;
            var exactPair = homeMatch.IsExactKeyMatch && awayMatch.IsExactKeyMatch;
            var leagueScore = ScoreMatchingHelper.GetLeagueMatchScore(league, candidateLeague);
            var timeScore = GetMatchTimeScore(targetMatchTime, matchTimeSelector(candidate));
            var statusScore = isLiveSelector(candidate) ? 0.0 : 0.30;
            var qualityScore = qualityScoreSelector?.Invoke(candidate) ?? 0.5;
            var totalScore = baseScore + (exactPair ? 0.20 : 0.0) + (leagueScore * 0.15) + (timeScore * 0.10) + statusScore + ((qualityScore - 0.5) * 0.10);

            scoredCandidates.Add((candidate, baseScore, totalScore, exactPair));
        }

        if (scoredCandidates.Count == 0)
        {
            return default;
        }

        var ordered = scoredCandidates
            .OrderByDescending(candidate => candidate.TotalScore)
            .ThenByDescending(candidate => candidate.BaseScore)
            .ToList();

        var best = ordered[0];
        if (!best.ExactPair && best.BaseScore < 0.84)
        {
            return default;
        }

        if (ordered.Count == 1)
        {
            return best.Candidate;
        }

        var runnerUp = ordered[1];
        var requiredMargin = best.ExactPair ? 0.05 : 0.12;
        return best.TotalScore - runnerUp.TotalScore >= requiredMargin
            ? best.Candidate
            : default;
    }

    private static bool ExactCandidateMatchesTargetDate<T>(
        T candidate,
        string? targetDate,
        Func<T, DateTime?> matchTimeSelector)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(targetDate))
        {
            return true;
        }

        var matchTime = matchTimeSelector(candidate);
        if (!matchTime.HasValue)
        {
            return false;
        }

        return string.Equals(
            DateTimeProvider.ConvertUtcToLocal(matchTime.Value).ToString("dd-MM-yyyy"),
            targetDate,
            StringComparison.Ordinal);
    }

    private static List<T> ConsolidateFixtureSnapshots<T>(
        IEnumerable<T> candidates,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, string?> leagueSelector,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, bool> isLiveSelector)
        where T : class
    {
        return candidates
            .GroupBy(candidate => CreateScoreFixtureKey(
                matchTimeSelector(candidate).HasValue
                    ? DateTimeProvider.ConvertUtcToLocal(matchTimeSelector(candidate)!.Value).ToString("dd-MM-yyyy")
                    : string.Empty,
                homeSelector(candidate),
                awaySelector(candidate),
                leagueSelector(candidate)))
            .Select(group => group
                .OrderBy(candidate => isLiveSelector(candidate) ? 1 : 0)
                .ThenByDescending(candidate => matchTimeSelector(candidate) ?? DateTime.MinValue)
                .First())
            .ToList();
    }

    private static (string Date, string Home, string Away, string League) GetStoredScoreSnapshotKey<T>(T score)
        where T : class
    {
        var (matchTime, homeTeam, awayTeam, league) = score switch
        {
            MatchScore flashScore => (flashScore.MatchTime, flashScore.HomeTeam, flashScore.AwayTeam, flashScore.League),
            AiScoreMatchScore aiScore => (aiScore.MatchTime, aiScore.HomeTeam, aiScore.AwayTeam, aiScore.League),
            SofaScoreMatchScore sofaScore => (sofaScore.MatchTime, sofaScore.HomeTeam, sofaScore.AwayTeam, sofaScore.League),
            _ => throw new ArgumentOutOfRangeException(nameof(score), "Unsupported stored score type.")
        };

        var localDate = DateTimeProvider.ConvertUtcToLocal(matchTime).ToString("dd-MM-yyyy");
        return CreateScoreFixtureKey(localDate, homeTeam, awayTeam, league);
    }

    private static DateTime ResolvePreferredStoredMatchTime(DateTime existingMatchTime, DateTime incomingMatchTime, bool existingIsLive, bool incomingIsLive)
    {
        if (!incomingIsLive)
        {
            return incomingMatchTime;
        }

        if (!existingIsLive)
        {
            return existingMatchTime;
        }

        return incomingMatchTime < existingMatchTime ? incomingMatchTime : existingMatchTime;
    }

    private static bool ShouldOverwriteStoredScore<T>(
        string existingScore,
        bool existingBttsLabel,
        bool existingIsLive,
        DateTime existingMatchTime,
        T incomingScore)
        where T : class
    {
        var incomingScoreValue = incomingScore switch
        {
            MatchScore flashScore => flashScore.Score,
            AiScoreMatchScore aiScore => aiScore.Score,
            SofaScoreMatchScore sofaScore => sofaScore.Score,
            _ => string.Empty
        };
        var incomingBttsLabel = incomingScore switch
        {
            MatchScore flashScore => flashScore.BTTSLabel,
            AiScoreMatchScore aiScore => aiScore.BTTSLabel,
            SofaScoreMatchScore sofaScore => sofaScore.BTTSLabel,
            _ => false
        };
        var incomingIsLive = incomingScore switch
        {
            MatchScore flashScore => flashScore.IsLive,
            AiScoreMatchScore aiScore => aiScore.IsLive,
            SofaScoreMatchScore sofaScore => sofaScore.IsLive,
            _ => true
        };
        var incomingMatchTime = incomingScore switch
        {
            MatchScore flashScore => flashScore.MatchTime,
            AiScoreMatchScore aiScore => aiScore.MatchTime,
            SofaScoreMatchScore sofaScore => sofaScore.MatchTime,
            _ => existingMatchTime
        };

        if (!existingIsLive && incomingIsLive)
        {
            return false;
        }

        if (existingIsLive && !incomingIsLive)
        {
            return true;
        }

        if (existingScore != incomingScoreValue || existingBttsLabel != incomingBttsLabel)
        {
            return incomingMatchTime >= existingMatchTime;
        }

        return false;
    }

    private static T? ResolveExtendedExactFinishedCandidate<T>(
        IReadOnlyList<RankedExactFinishedCandidate<T>> rankedCandidates)
        where T : class
    {
        if (rankedCandidates.Count == 0)
        {
            return default;
        }

        var best = rankedCandidates[0];
        if (best.MinutesApart <= ExactFinishedRepairWindowMinutes)
        {
            return best.Candidate;
        }

        if (best.MinutesApart > ExtendedExactFinishedRepairWindowMinutes)
        {
            return default;
        }

        var extendedWindowCandidates = rankedCandidates
            .Where(candidate => candidate.MinutesApart <= ExtendedExactFinishedRepairWindowMinutes)
            .ToList();

        if (extendedWindowCandidates.Count == 1)
        {
            return best.Candidate;
        }

        // Accept the best candidate only when every source within the extended window agrees on the
        // normalized score (unanimous consensus), reconciled through the shared voting layer.
        var consensus = ScoreConsensusResolver.Resolve(
            extendedWindowCandidates.Select((candidate, index) => new ScoreCandidate(
                SourceName: index.ToString(),
                NormalizedScore: NormalizeSettledScore(candidate.Score),
                IsFinished: true,
                Reliability: 1.0,
                ObservedAtUtc: DateTime.UtcNow)),
            consensusThreshold: 1.0);

        return consensus.HasConsensus && consensus.AgreeingSources == consensus.TotalSources
            ? best.Candidate
            : default;
    }

    private static string NormalizeSettledScore(string? score)
    {
        return string.IsNullOrWhiteSpace(score)
            ? string.Empty
            : score.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private object? ChooseBestExactSourceCandidate(
        SettlementFixtureGroup fixture,
        MatchScore? flashCandidate,
        AiScoreMatchScore? aiCandidate,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        if (flashCandidate is null)
        {
            return aiCandidate;
        }

        if (aiCandidate is null)
        {
            return flashCandidate;
        }

        var flashScore = GetExactSourceCandidateRank(
            fixture,
            "FlashScore",
            flashCandidate.League,
            flashCandidate.MatchTime,
            sourceQualityLookup);
        var aiScore = GetExactSourceCandidateRank(
            fixture,
            "AiScore",
            aiCandidate.League,
            aiCandidate.MatchTime,
            sourceQualityLookup);

        return aiScore - flashScore >= 0.05 ? aiCandidate : flashCandidate;
    }

    private object? ChooseBestLiveSourceCandidate(
        MatchScore? flashCandidate,
        AiScoreMatchScore? aiCandidate,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        if (flashCandidate is null)
        {
            return aiCandidate;
        }

        if (aiCandidate is null)
        {
            return flashCandidate;
        }

        var flashReliability = GetSourceQualityReliability(sourceQualityLookup, "FlashScore", flashCandidate.League, flashCandidate.MatchTime);
        var aiReliability = GetSourceQualityReliability(sourceQualityLookup, "AiScore", aiCandidate.League, aiCandidate.MatchTime);

        if (aiReliability - flashReliability >= 0.08)
        {
            return aiCandidate;
        }

        return aiReliability > flashReliability &&
               aiCandidate.MatchTime >= flashCandidate.MatchTime
            ? aiCandidate
            : flashCandidate;
    }

    private double GetExactSourceCandidateRank(
        SettlementFixtureGroup fixture,
        string sourceName,
        string? candidateLeague,
        DateTime? candidateMatchTimeUtc,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        var reliability = GetSourceQualityReliability(sourceQualityLookup, sourceName, candidateLeague, candidateMatchTimeUtc);
        var leagueScore = ScoreMatchingHelper.GetLeagueMatchScore(fixture.League, candidateLeague);
        var timeScore = GetMatchTimeScore(fixture.ScheduledMatchTimeUtc, candidateMatchTimeUtc);
        return (reliability * 0.55) + (timeScore * 0.30) + (leagueScore * 0.15);
    }
    private async Task SaveSofaScoreMatchScores(List<SofaScoreMatchScore> scores)
    {
        if (scores.Count == 0)
        {
            return;
        }

        var minTime = scores.Min(s => s.MatchTime);
        var maxTime = scores.Max(s => s.MatchTime);

        List<SofaScoreMatchScore> existingScoresList;
        try
        {
            existingScoresList = await _dbContext.SofaScoreMatchScores
                .Where(s => s.MatchTime >= minTime && s.MatchTime <= maxTime)
                .ToListAsync();
        }
        catch (PostgresException ex) when (IsMissingSofaScoreTable(ex))
        {
            _logger.LogWarning(
                "Skipping SofaScore score persistence because the SofaScoreMatchScores table is missing. Apply the latest EF migration to enable SofaScore source-quality history.");
            return;
        }

        var existingScoresDict = existingScoresList
            .GroupBy(GetStoredScoreSnapshotKey)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var incomingScore in scores)
        {
            var key = GetStoredScoreSnapshotKey(incomingScore);

            if (existingScoresDict.TryGetValue(key, out var existingRecord))
            {
                existingRecord.MatchTime = ResolvePreferredStoredMatchTime(
                    existingRecord.MatchTime,
                    incomingScore.MatchTime,
                    existingRecord.IsLive,
                    incomingScore.IsLive);

                if (ShouldOverwriteStoredScore(
                        existingRecord.Score,
                        existingRecord.BTTSLabel,
                        existingRecord.IsLive,
                        existingRecord.MatchTime,
                        incomingScore))
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                    existingRecord.BTTSLabel = incomingScore.BTTSLabel;
                    existingRecord.DisplayedScore = incomingScore.DisplayedScore;
                    existingRecord.RegularTimeScore = incomingScore.RegularTimeScore;
                    existingRecord.HalfTimeScore = incomingScore.HalfTimeScore;
                    existingRecord.ExtraTimeScore = incomingScore.ExtraTimeScore;
                    existingRecord.StatusText = incomingScore.StatusText;
                    existingRecord.EventUrl = incomingScore.EventUrl;
                    existingRecord.League = string.IsNullOrWhiteSpace(incomingScore.League)
                        ? existingRecord.League
                        : incomingScore.League;
                }
            }
            else
            {
                _dbContext.SofaScoreMatchScores.Add(incomingScore);
                existingScoresDict[key] = incomingScore;
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task<Dictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile>> LoadSourceQualityLookupAsync()
    {
        List<SourceQualityProfile> profiles;
        try
        {
            profiles = await _dbContext.SourceQualityProfiles
                .AsNoTracking()
                .ToListAsync();
        }
        catch (PostgresException ex) when (IsMissingSourceQualityTable(ex))
        {
            _logger.LogWarning(
                "Source quality lookup is unavailable because the SourceQualityProfiles table is missing. The score updater will continue without source-quality weighting until the latest EF migration is applied.");
            return [];
        }

        return profiles.ToDictionary(
            profile => (
                SourceName: NormalizeSourceName(profile.SourceName),
                LeagueKey: string.IsNullOrWhiteSpace(profile.LeagueKey) ? "all" : profile.LeagueKey.Trim().ToLowerInvariant(),
                TimeBucketKey: string.IsNullOrWhiteSpace(profile.TimeBucketKey) ? "all" : profile.TimeBucketKey.Trim().ToLowerInvariant()),
            profile => profile);
    }
    private sealed class SettlementFixtureGroup
    {
        public string Date { get; init; } = string.Empty;
        public DateOnly MatchLocalDate { get; init; }
        public string FixtureKey { get; init; } = string.Empty;
        public string HomeTeam { get; init; } = string.Empty;
        public string AwayTeam { get; init; } = string.Empty;
        public string League { get; init; } = string.Empty;
        public DateTime? ScheduledMatchTimeUtc { get; init; }
        public List<Prediction> Predictions { get; } = [];
        public List<ForecastObservation> Forecasts { get; } = [];
    }

    private static bool IsSofaScoreIndexRowTooLarge(DbUpdateException ex)
    {
        if (ex.InnerException is not PostgresException postgresEx)
        {
            return false;
        }

        return postgresEx.SqlState == PostgresErrorCodes.ProgramLimitExceeded &&
               string.Equals(postgresEx.ConstraintName, "IX_SofaScoreMatchScores_MatchTime_HomeTeam_AwayTeam", StringComparison.Ordinal);
    }

    private sealed record RankedExactFinishedCandidate<T>(
        T Candidate,
        DateTime? MatchTime,
        double MinutesApart,
        double LeagueScore,
        string? Score,
        double QualityScore)
        where T : class;
    private sealed class FixtureCandidateIndex<T>
        where T : class
    {
        private readonly Dictionary<(string HomeKey, string AwayKey), List<T>> _exactPairLookup;
        private readonly Dictionary<string, List<T>> _dateLookup;
        private readonly Dictionary<(string Date, string LeagueKey), List<T>> _dateLeagueLookup;

        public FixtureCandidateIndex(
            IEnumerable<T> candidates,
            Func<T, string> homeSelector,
            Func<T, string> awaySelector,
            Func<T, string?> leagueSelector,
            Func<T, DateTime?> matchTimeSelector)
        {
            AllCandidates = candidates.ToList();

            _exactPairLookup = AllCandidates
                .GroupBy(candidate => (
                    HomeKey: ScoreMatchingHelper.CreateTeamLookupKey(homeSelector(candidate), leagueSelector(candidate)),
                    AwayKey: ScoreMatchingHelper.CreateTeamLookupKey(awaySelector(candidate), leagueSelector(candidate))))
                .ToDictionary(group => group.Key, group => group.ToList());

            _dateLookup = AllCandidates
                .Where(candidate => matchTimeSelector(candidate).HasValue)
                .GroupBy(candidate => DateTimeProvider.ConvertUtcToLocal(matchTimeSelector(candidate)!.Value).ToString("dd-MM-yyyy"))
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            _dateLeagueLookup = AllCandidates
                .Where(candidate => matchTimeSelector(candidate).HasValue)
                .GroupBy(candidate => (
                    Date: DateTimeProvider.ConvertUtcToLocal(matchTimeSelector(candidate)!.Value).ToString("dd-MM-yyyy"),
                    LeagueKey: ScoreMatchingHelper.CreateLeagueLookupKey(leagueSelector(candidate))))
                .ToDictionary(group => group.Key, group => group.ToList());
        }

        public IReadOnlyList<T> AllCandidates { get; }

        public IReadOnlyList<T> GetExactPairCandidates(string homeKey, string awayKey)
        {
            return _exactPairLookup.TryGetValue((homeKey, awayKey), out var candidates)
                ? candidates
                : [];
        }

        public IReadOnlyList<T> GetScopedCandidates(string? targetDate, string? league, DateTime? targetMatchTime)
        {
            var datesToTry = new List<string>();

            if (!string.IsNullOrWhiteSpace(targetDate))
            {
                datesToTry.Add(targetDate);
            }

            if (targetMatchTime.HasValue)
            {
                var derivedDate = DateTimeProvider.ConvertUtcToLocal(targetMatchTime.Value).ToString("dd-MM-yyyy");
                if (!datesToTry.Contains(derivedDate, StringComparer.Ordinal))
                {
                    datesToTry.Add(derivedDate);
                }
            }

            var leagueKey = ScoreMatchingHelper.CreateLeagueLookupKey(league);

            foreach (var date in datesToTry)
            {
                if (!string.IsNullOrWhiteSpace(leagueKey) &&
                    _dateLeagueLookup.TryGetValue((date, leagueKey), out var dateLeagueCandidates) &&
                    dateLeagueCandidates.Count > 0)
                {
                    return dateLeagueCandidates;
                }

                if (_dateLookup.TryGetValue(date, out var dateCandidates) && dateCandidates.Count > 0)
                {
                    return dateCandidates;
                }
            }

            return AllCandidates;
        }
    }

}
