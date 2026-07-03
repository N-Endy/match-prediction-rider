using System.Globalization;
using System.Text.Json;
using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
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
    [DisableConcurrentExecution(AnalyzerJobResource, 3600)]
    public async Task GeneratePredictionsAsync(string? targetDate = null, string? runReason = null)
    {
        var targetDateString = ResolveTargetDateString(targetDate);
        var targetLocalDate = ParseTargetLocalDate(targetDateString);
        var normalizedRunReason = string.IsNullOrWhiteSpace(runReason) ? "prediction-generation" : runReason.Trim();

        _logger.LogInformation("Starting prediction generation process for target date {TargetDate}.", targetDateString);
        try
        {
            // Today-only canonical repair so freshly synced fixtures dedupe correctly.
            // The 7/30-day historical backfills run once nightly in RunDailyAnalysisAsync
            // instead of on every generation pass (5x/day).
            await BackfillCanonicalFixtureFieldsAsync(0);

            var matches = await _dbContext.MatchDatas
                .Where(match => match.MatchLocalDate == targetLocalDate)
                .ToListAsync();

            foreach (var match in matches)
            {
                ApplyCanonicalFixtureIdentity(match);
            }

            var generationMatches = DeduplicateMatchesForGeneration(matches, targetDateString);
            IReadOnlyList<SourceMarketFixture> publishPricingFixtures = [];
            try
            {
                publishPricingFixtures = await _sourceMarketPricingService.GetSourceMarketFixturesForDateAsync(targetLocalDate);
            }
            catch (Exception pricingEx)
            {
                _logger.LogWarning(pricingEx, "Failed to load live source pricing while generating predictions for {TargetDate}. Publish odds snapshots will fall back to derived pricing.", targetDateString);
            }

            var bookmakerSignals = BookmakerSignalSetBuilder.Build(generationMatches, publishPricingFixtures);
            if (bookmakerSignals.Count > 0)
            {
                _logger.LogInformation(
                    "Resolved de-vigged bookmaker signals for {SignalCount}/{MatchCount} fixture(s) on {TargetDate}.",
                    bookmakerSignals.Count,
                    generationMatches.Count,
                    targetDateString);
            }

            var forecastCandidates = _dataAnalyzerService.BuildForecastCandidates(generationMatches, bookmakerSignals).ToList();
            foreach (var candidate in forecastCandidates)
            {
                ApplyCanonicalFixtureIdentity(candidate);
            }
            forecastCandidates = DeduplicateForecastCandidates(forecastCandidates, targetDateString);

            var publishedCandidates = _dataAnalyzerService.SelectPublishedPredictions(forecastCandidates).ToList();
            foreach (var candidate in publishedCandidates)
            {
                ApplyCanonicalFixtureIdentity(candidate);
            }
            publishedCandidates = DeduplicatePublishedCandidates(publishedCandidates, targetDateString);

            var predictionRun = await CreatePredictionRunAsync(targetLocalDate, normalizedRunReason, forecastCandidates, publishedCandidates);
            await SaveForecastObservations(forecastCandidates, publishedCandidates, predictionRun);
            var savedPredictions = await SavePredictions(forecastCandidates, publishedCandidates, predictionRun);
            await CapturePublishOddsSnapshotsAsync(savedPredictions, generationMatches, publishPricingFixtures);
            predictionRun.Succeeded = true;
            predictionRun.CompletedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            
            _logger.LogInformation("✅ Predictions calculated and saved successfully for target date {TargetDate}.", targetDateString);
            await LogScrapingStatus(
                PredictionGenerationEventName,
                "Success",
                $"✅ Prediction generation completed successfully for {targetDateString}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during prediction calculations.");
            await LogScrapingStatus(PredictionGenerationEventName, "Failed", $"Prediction Gen Error: {ex.Message}");
            throw;
        }
    }
    private static string ResolveTargetDateString(string? targetDate)
    {
        if (string.IsNullOrWhiteSpace(targetDate))
        {
            return DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParseExact(
                targetDate.Trim(),
                ["dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            return parsedDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        }

        throw new FormatException($"Invalid target date format: '{targetDate}'. Expected dd-MM-yyyy.");
    }

    private static DateOnly ParseTargetLocalDate(string targetDate)
    {
        return DateOnly.ParseExact(targetDate, "dd-MM-yyyy", CultureInfo.InvariantCulture);
    }
    private async Task<IReadOnlyList<Prediction>> SavePredictions(
        IEnumerable<PredictionCandidate> forecastCandidates,
        IEnumerable<PredictionCandidate> candidates,
        PredictionRun predictionRun)
    {
        var forecastCandidateList = forecastCandidates.ToList();
        var candidateList = DeduplicatePublishedCandidates(candidates, predictionRun.TargetLocalDate.ToString("dd-MM-yyyy"));
        var nowUtc = DateTime.UtcNow;
        var touchedFixtureKeys = BuildCandidateFixtureKeySet(forecastCandidateList);
        var lockedFixtureKeys = BuildLockedCandidateFixtureKeySet(forecastCandidateList, nowUtc);
        if (touchedFixtureKeys.Count == 0)
        {
            touchedFixtureKeys = BuildCandidateFixtureKeySet(candidateList);
            lockedFixtureKeys = BuildLockedCandidateFixtureKeySet(candidateList, nowUtc);
        }

        if (touchedFixtureKeys.Count == 0)
        {
            return [];
        }

        var targetDate = predictionRun.TargetLocalDate;
        var currentPredictions = await _dbContext.Predictions
            .Where(p => p.MatchLocalDate == targetDate && p.IsCurrentRevision)
            .ToListAsync();
        var historicalPredictions = await _dbContext.Predictions
            .Where(p => p.MatchLocalDate == targetDate)
            .ToListAsync();

        var currentByKey = currentPredictions
            .GroupBy(GetPredictionKey)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(prediction => prediction.RevisionNumber)
                    .ThenByDescending(prediction => prediction.CreatedAt)
                    .ThenByDescending(prediction => prediction.Id)
                    .First());
        var historicalByKey = historicalPredictions
            .GroupBy(GetPredictionKey)
            .ToDictionary(group => group.Key, group => group.ToList());
        var revisionByKey = historicalPredictions
            .GroupBy(GetPredictionKey)
            .ToDictionary(group => group.Key, group => group.Max(prediction => prediction.RevisionNumber));

        RestoreLockedPredictionCurrents(candidateList, currentByKey, historicalByKey, nowUtc);

        var createdPredictions = new List<Prediction>();
        foreach (var existingRecord in currentPredictions.Where(prediction =>
                     touchedFixtureKeys.Contains(GetPredictionFixtureKey(prediction)) &&
                     !lockedFixtureKeys.Contains(GetPredictionFixtureKey(prediction))))
        {
            existingRecord.IsCurrentRevision = false;
            existingRecord.SupersededAt = nowUtc;
        }

        var skippedLockedCandidates = 0;
        foreach (var candidate in candidateList)
        {
            if (lockedFixtureKeys.Contains(GetCandidateFixtureKey(candidate)))
            {
                skippedLockedCandidates++;
                continue;
            }

            var currentKey = GetCandidatePredictionKey(candidate);
            currentByKey.TryGetValue(currentKey, out var currentRecord);
            var nextRevision = revisionByKey.TryGetValue(currentKey, out var revisionNumber)
                ? revisionNumber + 1
                : 1;

            var prediction = new Prediction
            {
                HomeTeam = candidate.HomeTeam.Trim(),
                AwayTeam = candidate.AwayTeam.Trim(),
                League = candidate.League.Trim(),
                PredictionCategory = candidate.PredictionCategory,
                PredictedOutcome = candidate.PredictedOutcome,
                RawConfidenceScore = Math.Round((decimal)candidate.RawProbability, 4),
                ConfidenceScore = Math.Round((decimal)candidate.CalibratedProbability, 4),
                CalibratorUsed = candidate.CalibratorUsed,
                ThresholdUsed = Math.Round(candidate.ThresholdUsed, 4),
                ThresholdSource = candidate.ThresholdSource,
                WasPublished = candidate.WasPublished,
                Date = candidate.Date,
                Time = candidate.Time,
                MatchLocalDate = candidate.MatchLocalDate,
                MatchLocalTime = candidate.MatchLocalTime,
                MatchDateTime = candidate.MatchDateTime,
                FixtureKey = candidate.FixtureKey,
                PredictionRunId = predictionRun.Id,
                RunLabel = predictionRun.RunLabel,
                RunReason = predictionRun.RunReason,
                RevisionNumber = nextRevision,
                IsCurrentRevision = true,
                ActualOutcome = currentRecord?.ActualOutcome,
                ActualScore = currentRecord?.ActualScore,
                IsLive = currentRecord?.IsLive ?? false
            };

            createdPredictions.Add(prediction);
            _dbContext.Predictions.Add(prediction);
        }

        if (skippedLockedCandidates > 0)
        {
            _logger.LogInformation(
                "Skipped {SkippedCount} post-kickoff published prediction candidate(s) to preserve the pre-kickoff current revision.",
                skippedLockedCandidates);
        }

        await _dbContext.SaveChangesAsync();
        return createdPredictions;
    }

    private async Task SaveForecastObservations(
        IEnumerable<PredictionCandidate> forecastCandidates,
        IEnumerable<PredictionCandidate> publishedCandidates,
        PredictionRun predictionRun)
    {
        var forecastList = DeduplicateForecastCandidates(
            forecastCandidates,
            predictionRun.TargetLocalDate.ToString("dd-MM-yyyy"));
        if (!forecastList.Any()) return;

        var nowUtc = DateTime.UtcNow;
        var publishedKeys = publishedCandidates
            .Select(GetCandidateObservationKey)
            .ToHashSet(StringComparer.Ordinal);

        var targetDate = predictionRun.TargetLocalDate;
        var currentForecasts = await _dbContext.ForecastObservations
            .Where(forecast => forecast.MatchLocalDate == targetDate && forecast.IsCurrentRevision)
            .ToListAsync();
        var historicalForecasts = await _dbContext.ForecastObservations
            .Where(forecast => forecast.MatchLocalDate == targetDate)
            .ToListAsync();

        var currentByKey = currentForecasts
            .GroupBy(GetObservationKey)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(forecast => forecast.RevisionNumber)
                    .ThenByDescending(forecast => forecast.CreatedAt)
                    .ThenByDescending(forecast => forecast.Id)
                    .First());
        var historicalByKey = historicalForecasts
            .GroupBy(GetObservationKey)
            .ToDictionary(group => group.Key, group => group.ToList());
        var revisionByKey = historicalForecasts
            .GroupBy(GetObservationKey)
            .ToDictionary(group => group.Key, group => group.Max(forecast => forecast.RevisionNumber));
        var touchedFixtureKeys = BuildCandidateFixtureKeySet(forecastList);
        var lockedFixtureKeys = BuildLockedCandidateFixtureKeySet(forecastList, nowUtc);

        RestoreLockedForecastCurrents(forecastList, currentByKey, historicalByKey, nowUtc);

        foreach (var existingRecord in currentForecasts.Where(forecast =>
                     touchedFixtureKeys.Contains(GetForecastFixtureKey(forecast)) &&
                     !lockedFixtureKeys.Contains(GetForecastFixtureKey(forecast))))
        {
            existingRecord.IsCurrentRevision = false;
            existingRecord.SupersededAt = nowUtc;
        }

        var skippedLockedForecasts = 0;
        foreach (var candidate in forecastList)
        {
            if (lockedFixtureKeys.Contains(GetCandidateFixtureKey(candidate)))
            {
                skippedLockedForecasts++;
                continue;
            }

            var key = GetCandidateObservationKey(candidate);
            var isPublished = publishedKeys.Contains(key);
            currentByKey.TryGetValue(key, out var currentRecord);
            var nextRevision = revisionByKey.TryGetValue(key, out var revisionNumber)
                ? revisionNumber + 1
                : 1;

            _dbContext.ForecastObservations.Add(new ForecastObservation
            {
                Date = candidate.Date,
                Time = candidate.Time,
                MatchLocalDate = candidate.MatchLocalDate,
                MatchLocalTime = candidate.MatchLocalTime,
                MatchDateTime = candidate.MatchDateTime,
                FixtureKey = candidate.FixtureKey,
                League = candidate.League,
                HomeTeam = candidate.HomeTeam,
                AwayTeam = candidate.AwayTeam,
                Market = candidate.Market,
                PredictedOutcome = candidate.PredictedOutcome,
                RawProbability = candidate.RawProbability,
                CorrectedProbability = candidate.CorrectedProbability,
                CalibratedProbability = candidate.CalibratedProbability,
                CalibratorUsed = candidate.CalibratorUsed,
                ThresholdUsed = Math.Round(candidate.ThresholdUsed, 4),
                ThresholdSource = candidate.ThresholdSource,
                FeatureContributionsJson = candidate.FeatureContributionsJson,
                IsPublished = isPublished,
                PredictionRunId = predictionRun.Id,
                RunLabel = predictionRun.RunLabel,
                RunReason = predictionRun.RunReason,
                RevisionNumber = nextRevision,
                IsCurrentRevision = true,
                ActualOutcome = currentRecord?.ActualOutcome,
                ActualScore = currentRecord?.ActualScore,
                OutcomeOccurred = currentRecord?.OutcomeOccurred,
                IsLive = currentRecord?.IsLive ?? false,
                IsSettled = currentRecord?.IsSettled ?? false,
                SettledAt = currentRecord?.SettledAt
            });
        }

        if (skippedLockedForecasts > 0)
        {
            _logger.LogInformation(
                "Skipped {SkippedCount} post-kickoff forecast candidate(s) to preserve the pre-kickoff current revision.",
                skippedLockedForecasts);
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task CapturePublishOddsSnapshotsAsync(
        IReadOnlyList<Prediction> predictions,
        IReadOnlyCollection<MatchData> matchDatas,
        IReadOnlyList<SourceMarketFixture> sourceFixtures)
    {
        if (predictions.Count == 0)
        {
            return;
        }

        var createdCount = await SavePredictionOddsSnapshotsAsync(predictions, matchDatas, sourceFixtures, PredictionOddsSnapshotKind.Publish);
        if (createdCount > 0)
        {
            _logger.LogInformation("Captured {Count} publish odds snapshot(s) for the latest prediction run.", createdCount);
        }
    }

    private async Task<int> SavePredictionOddsSnapshotsAsync(
        IReadOnlyCollection<Prediction> predictions,
        IReadOnlyCollection<MatchData> matchDatas,
        IReadOnlyList<SourceMarketFixture> sourceFixtures,
        PredictionOddsSnapshotKind snapshotKind)
    {
        if (predictions.Count == 0)
        {
            return 0;
        }

        var predictionIds = predictions.Select(prediction => prediction.Id).ToList();
        var existingKeys = await _dbContext.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(snapshot =>
                predictionIds.Contains(snapshot.PredictionId) &&
                snapshot.SnapshotKind == snapshotKind)
            .Select(snapshot => new { snapshot.PredictionId, snapshot.SourceName })
            .ToListAsync();

        var existingKeySet = existingKeys
            .Select(snapshot => BuildSnapshotDuplicateKey(snapshot.PredictionId, snapshot.SourceName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byFixtureAndLeague = matchDatas
            .GroupBy(match => BuildPredictionMatchKey(match.MatchLocalDate, match.FixtureKey, match.League, match.HomeTeam, match.AwayTeam, includeLeague: true))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var byFixture = matchDatas
            .GroupBy(match => BuildPredictionMatchKey(match.MatchLocalDate, match.FixtureKey, null, match.HomeTeam, match.AwayTeam, includeLeague: false))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var createdSnapshots = new List<PredictionOddsSnapshot>();
        foreach (var prediction in predictions)
        {
            var matchData = ResolveMatchDataForPrediction(prediction, byFixtureAndLeague, byFixture);
            if (matchData is null)
            {
                continue;
            }

            var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
                sourceFixtures,
                prediction.HomeTeam,
                prediction.AwayTeam,
                prediction.League,
                prediction.MatchDateTime);

            if (!MarketQuoteResolver.TryResolve(matchData, prediction, sourceFixture, out var quote))
            {
                continue;
            }

            var duplicateKey = BuildSnapshotDuplicateKey(prediction.Id, quote.SourceName);
            if (existingKeySet.Contains(duplicateKey))
            {
                continue;
            }

            createdSnapshots.Add(new PredictionOddsSnapshot
            {
                PredictionId = prediction.Id,
                PredictionRunId = prediction.PredictionRunId,
                SourceName = quote.SourceName,
                Market = prediction.PredictionCategory,
                Outcome = prediction.PredictedOutcome,
                DecimalOdds = Math.Round(quote.DecimalOdds, 4),
                ImpliedProbability = Math.Round(quote.ImpliedProbability, 6),
                OddsDerivationSource = quote.OddsDerivationSource,
                SnapshotKind = snapshotKind,
                CapturedAtUtc = DateTime.UtcNow
            });

            existingKeySet.Add(duplicateKey);
        }

        if (createdSnapshots.Count == 0)
        {
            return 0;
        }

        _dbContext.PredictionOddsSnapshots.AddRange(createdSnapshots);
        await _dbContext.SaveChangesAsync();
        return createdSnapshots.Count;
    }

    private static string GetObservationKey(ForecastObservation forecast)
    {
        return $"{ResolveFixtureKey(forecast.FixtureKey, forecast.MatchLocalDate, forecast.League, forecast.HomeTeam, forecast.AwayTeam)}|{forecast.Market}";
    }

    private static string GetPredictionKey(Prediction prediction)
    {
        return $"{GetPredictionFixtureKey(prediction)}|{prediction.PredictionCategory}";
    }

    private static string GetPredictionFixtureKey(Prediction prediction)
    {
        return ResolveFixtureKey(
            prediction.FixtureKey,
            prediction.MatchLocalDate,
            prediction.League,
            prediction.HomeTeam,
            prediction.AwayTeam);
    }

    private static string GetForecastFixtureKey(ForecastObservation forecast)
    {
        return ResolveFixtureKey(
            forecast.FixtureKey,
            forecast.MatchLocalDate,
            forecast.League,
            forecast.HomeTeam,
            forecast.AwayTeam);
    }

    private static string GetCandidateFixtureKey(PredictionCandidate candidate)
    {
        return ResolveFixtureKey(
            candidate.FixtureKey,
            candidate.MatchLocalDate,
            candidate.League,
            candidate.HomeTeam,
            candidate.AwayTeam);
    }

    private static HashSet<string> BuildCandidateFixtureKeySet(IEnumerable<PredictionCandidate> candidates)
    {
        return candidates
            .Select(GetCandidateFixtureKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> BuildLockedCandidateFixtureKeySet(IEnumerable<PredictionCandidate> candidates, DateTime nowUtc)
    {
        return candidates
            .Where(candidate => IsFixturePastKickoffGrace(candidate.MatchDateTime, nowUtc))
            .Select(GetCandidateFixtureKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    private void RestoreLockedPredictionCurrents(
        IEnumerable<PredictionCandidate> candidates,
        IDictionary<string, Prediction> currentByKey,
        IReadOnlyDictionary<string, List<Prediction>> historicalByKey,
        DateTime nowUtc)
    {
        foreach (var lockedKey in candidates
                     .Where(candidate => IsFixturePastKickoffGrace(candidate.MatchDateTime, nowUtc))
                     .Select(GetCandidatePredictionKey)
                     .Distinct(StringComparer.Ordinal))
        {
            if (!currentByKey.TryGetValue(lockedKey, out var currentRecord) ||
                IsEligiblePreKickoffSnapshot(currentRecord.MatchDateTime, currentRecord.CreatedAt))
            {
                continue;
            }

            if (!historicalByKey.TryGetValue(lockedKey, out var history))
            {
                continue;
            }

            var restoredRecord = history
                .Where(record => record.Id != currentRecord.Id && IsEligiblePreKickoffSnapshot(record.MatchDateTime, record.CreatedAt))
                .OrderByDescending(record => record.RevisionNumber)
                .ThenByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.Id)
                .FirstOrDefault();

            if (restoredRecord is null)
            {
                continue;
            }

            restoredRecord.IsCurrentRevision = true;
            restoredRecord.SupersededAt = null;
            restoredRecord.ActualOutcome = currentRecord.ActualOutcome;
            restoredRecord.ActualScore = currentRecord.ActualScore;
            restoredRecord.IsLive = currentRecord.IsLive;

            currentRecord.IsCurrentRevision = false;
            currentRecord.SupersededAt = nowUtc;
            currentByKey[lockedKey] = restoredRecord;
        }
    }

    private void RestoreLockedForecastCurrents(
        IEnumerable<PredictionCandidate> candidates,
        IDictionary<string, ForecastObservation> currentByKey,
        IReadOnlyDictionary<string, List<ForecastObservation>> historicalByKey,
        DateTime nowUtc)
    {
        foreach (var lockedKey in candidates
                     .Where(candidate => IsFixturePastKickoffGrace(candidate.MatchDateTime, nowUtc))
                     .Select(GetCandidateObservationKey)
                     .Distinct(StringComparer.Ordinal))
        {
            if (!currentByKey.TryGetValue(lockedKey, out var currentRecord) ||
                IsEligiblePreKickoffSnapshot(currentRecord.MatchDateTime, currentRecord.CreatedAt))
            {
                continue;
            }

            if (!historicalByKey.TryGetValue(lockedKey, out var history))
            {
                continue;
            }

            var restoredRecord = history
                .Where(record => record.Id != currentRecord.Id && IsEligiblePreKickoffSnapshot(record.MatchDateTime, record.CreatedAt))
                .OrderByDescending(record => record.RevisionNumber)
                .ThenByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.Id)
                .FirstOrDefault();

            if (restoredRecord is null)
            {
                continue;
            }

            restoredRecord.IsCurrentRevision = true;
            restoredRecord.SupersededAt = null;
            restoredRecord.ActualOutcome = currentRecord.ActualOutcome;
            restoredRecord.ActualScore = currentRecord.ActualScore;
            restoredRecord.OutcomeOccurred = currentRecord.OutcomeOccurred;
            restoredRecord.IsLive = currentRecord.IsLive;
            restoredRecord.IsSettled = currentRecord.IsSettled;
            restoredRecord.SettledAt = currentRecord.SettledAt;

            currentRecord.IsCurrentRevision = false;
            currentRecord.SupersededAt = nowUtc;
            currentByKey[lockedKey] = restoredRecord;
        }
    }

    private static bool IsFixturePastKickoffGrace(DateTime? kickoffUtc, DateTime nowUtc)
    {
        return kickoffUtc.HasValue && nowUtc > kickoffUtc.Value.Add(CurrentRevisionKickoffGrace);
    }

    private static bool IsEligiblePreKickoffSnapshot(DateTime? kickoffUtc, DateTime createdAtUtc)
    {
        return !kickoffUtc.HasValue || createdAtUtc <= kickoffUtc.Value.Add(CurrentRevisionKickoffGrace);
    }

    private static string GetCandidateObservationKey(PredictionCandidate candidate)
    {
        return $"{GetCandidateFixtureKey(candidate)}|{candidate.Market}";
    }

    private static string GetCandidatePredictionKey(PredictionCandidate candidate)
    {
        return $"{GetCandidateFixtureKey(candidate)}|{candidate.PredictionCategory}";
    }

    private static MatchData? ResolveMatchDataForPrediction(
        Prediction prediction,
        IReadOnlyDictionary<string, List<MatchData>> byFixtureAndLeague,
        IReadOnlyDictionary<string, List<MatchData>> byFixture)
    {
        var leagueKey = BuildPredictionMatchKey(prediction.MatchLocalDate, prediction.FixtureKey, prediction.League, prediction.HomeTeam, prediction.AwayTeam, includeLeague: true);
        if (byFixtureAndLeague.TryGetValue(leagueKey, out var leagueMatches))
        {
            return SelectBestSnapshotMatchData(leagueMatches, prediction);
        }

        var fixtureKey = BuildPredictionMatchKey(prediction.MatchLocalDate, prediction.FixtureKey, null, prediction.HomeTeam, prediction.AwayTeam, includeLeague: false);
        return byFixture.TryGetValue(fixtureKey, out var fallbackMatches)
            ? SelectBestSnapshotMatchData(fallbackMatches, prediction)
            : null;
    }

    private static MatchData SelectBestSnapshotMatchData(IEnumerable<MatchData> matches, Prediction prediction)
    {
        return matches
            .OrderBy(match => match.MatchDateTime.HasValue ? Math.Abs((match.MatchDateTime.Value - (prediction.MatchDateTime ?? match.MatchDateTime.Value)).TotalMinutes) : double.MaxValue)
            .ThenBy(match => string.Equals(match.League, prediction.League, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First();
    }

    private static string BuildPredictionMatchKey(
        DateOnly? matchLocalDate,
        string? fixtureKey,
        string? league,
        string? homeTeam,
        string? awayTeam,
        bool includeLeague)
    {
        return includeLeague
            ? ResolveFixtureKey(fixtureKey, matchLocalDate, league, homeTeam, awayTeam)
            : ResolveFixtureKey(fixtureKey, matchLocalDate, null, homeTeam, awayTeam);
    }

    private static string BuildSnapshotDuplicateKey(int predictionId, string sourceName)
    {
        return $"{predictionId}|{sourceName}";
    }

    private List<MatchData> DeduplicateMatchesForGeneration(IEnumerable<MatchData> matches, string targetDate)
    {
        return DeduplicateItems(
            matches,
            GetMatchIdentityKey,
            SelectPreferredMatchData,
            duplicateGroups =>
            {
                _logger.LogWarning(
                    "Deduplicated {DuplicateGroupCount} duplicate match fixture groups before prediction generation for {TargetDate}. Samples: {Samples}",
                    duplicateGroups.Count,
                    targetDate,
                    FormatDuplicateSamples(duplicateGroups.Select(group => group.First()), DescribeMatchData));
            });
    }

    private List<PredictionCandidate> DeduplicateForecastCandidates(IEnumerable<PredictionCandidate> candidates, string targetDate)
    {
        return DeduplicateItems(
            candidates,
            GetCandidateObservationKey,
            SelectPreferredCandidate,
            duplicateGroups =>
            {
                _logger.LogWarning(
                    "Deduplicated {DuplicateGroupCount} duplicate forecast candidate groups for {TargetDate}. Samples: {Samples}",
                    duplicateGroups.Count,
                    targetDate,
                    FormatDuplicateSamples(duplicateGroups.Select(group => group.First()), DescribeCandidate));
            });
    }

    private List<PredictionCandidate> DeduplicatePublishedCandidates(IEnumerable<PredictionCandidate> candidates, string targetDate)
    {
        return DeduplicateItems(
            candidates,
            GetCandidatePredictionKey,
            SelectPreferredCandidate,
            duplicateGroups =>
            {
                _logger.LogWarning(
                    "Deduplicated {DuplicateGroupCount} duplicate published prediction groups for {TargetDate}. Samples: {Samples}",
                    duplicateGroups.Count,
                    targetDate,
                    FormatDuplicateSamples(duplicateGroups.Select(group => group.First()), DescribeCandidate));
            });
    }

    private static List<T> DeduplicateItems<T>(
        IEnumerable<T> items,
        Func<T, string> keySelector,
        Func<IEnumerable<T>, T> winnerSelector,
        Action<List<IGrouping<string, T>>> logDuplicates)
    {
        var groups = items
            .GroupBy(keySelector, StringComparer.Ordinal)
            .ToList();

        var duplicateGroups = groups
            .Where(group => group.Count() > 1)
            .ToList();

        if (duplicateGroups.Count > 0)
        {
            logDuplicates(duplicateGroups);
        }

        return groups
            .Select(winnerSelector)
            .ToList();
    }

    private static PredictionCandidate SelectPreferredCandidate(IEnumerable<PredictionCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.WasPublished)
            .ThenByDescending(candidate => candidate.CalibratedProbability)
            .ThenByDescending(candidate => candidate.RawProbability)
            .ThenByDescending(candidate => candidate.ThresholdUsed)
            .ThenByDescending(candidate => candidate.MatchDateTime ?? DateTime.MinValue)
            .ThenByDescending(candidate => candidate.MatchLocalTime ?? TimeOnly.MinValue)
            .First();
    }

    private static MatchData SelectPreferredMatchData(IEnumerable<MatchData> matches)
    {
        return matches
            .OrderByDescending(GetMatchDataCompletenessScore)
            .ThenByDescending(match => match.MatchDateTime ?? DateTime.MinValue)
            .ThenByDescending(match => match.Id)
            .First();
    }

    private static int GetMatchDataCompletenessScore(MatchData match)
    {
        var score = 0;
        score += !string.IsNullOrWhiteSpace(match.FixtureKey) ? 4 : 0;
        score += match.MatchDateTime.HasValue ? 3 : 0;
        score += match.MatchLocalTime.HasValue ? 1 : 0;
        score += CountPositive(
            match.HomeWin,
            match.Draw,
            match.AwayWin,
            match.OverTwoGoals,
            match.UnderTwoGoals,
            match.OverThreeGoals,
            match.UnderThreeGoals,
            match.BttsYes,
            match.BttsNo,
            match.OverOneGoal,
            match.OverOnePointFive,
            match.UnderOnePointFive,
            match.AhZeroHome,
            match.AhZeroAway,
            match.AhMinusHalfHome,
            match.AhMinusHalfAway,
            match.AhMinusOneHome,
            match.AhMinusOneAway,
            match.AhPlusHalfHome,
            match.AhPlusHalfAway);
        return score;
    }

    private static int CountPositive(params double[] values)
    {
        return values.Count(value => value > 0);
    }

    private static string GetMatchIdentityKey(MatchData match)
    {
        return ResolveFixtureKey(
            match.FixtureKey,
            match.MatchLocalDate,
            match.League,
            match.HomeTeam,
            match.AwayTeam);
    }

    private static string ResolveFixtureKey(
        string? fixtureKey,
        DateOnly? localDate,
        string? league,
        string? homeTeam,
        string? awayTeam)
    {
        if (!string.IsNullOrWhiteSpace(fixtureKey))
        {
            return fixtureKey.Trim();
        }

        return string.Join(
            "|",
            localDate?.ToString("yyyy-MM-dd") ?? "unknown-date",
            NormalizeFixtureKeyPart(league),
            NormalizeFixtureKeyPart(homeTeam),
            NormalizeFixtureKeyPart(awayTeam));
    }

    private static string NormalizeFixtureKeyPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();
    }

    private static string FormatDuplicateSamples<T>(IEnumerable<T> items, Func<T, string> formatter)
    {
        return string.Join("; ", items.Take(5).Select(formatter));
    }

    private static string DescribeCandidate(PredictionCandidate candidate)
    {
        return $"{candidate.HomeTeam} vs {candidate.AwayTeam} [{candidate.League}] {candidate.PredictionCategory}/{candidate.Market} key={candidate.FixtureKey} cal={candidate.CalibratedProbability:F3}";
    }

    private static string DescribeMatchData(MatchData match)
    {
        return $"{match.HomeTeam} vs {match.AwayTeam} [{match.League}] key={match.FixtureKey} time={match.Time} score={GetMatchDataCompletenessScore(match)}";
    }
    private async Task SaveRegressionPredictions(IEnumerable<RegressionPrediction> predictions)
    {
        var predictionList = predictions.ToList();
        if (!predictionList.Any()) return;

        // 1. Extract unique dates to fetch existing records in ONE bulk query
        var uniqueDates = predictionList.Select(p => p.Date).Distinct().ToList();

        var existingPredictions = await _dbContext.RegressionPredictions
            .Where(p => uniqueDates.Contains(p.Date))
            .ToListAsync();

        // 2. Create a Dictionary for O(1) memory lookups using your 6-part composite key
        var existingDict = existingPredictions
            .GroupBy(p => (
                p.HomeTeam, 
                p.AwayTeam, 
                p.League, 
                p.Date, 
                p.Time, 
                p.PredictionCategory))
            .ToDictionary(g => g.Key, g => g.First());

        // 3. Loop through the memory collection, not the database
        foreach (var prediction in predictionList)
        {
            var key = (
                prediction.HomeTeam, 
                prediction.AwayTeam, 
                prediction.League, 
                prediction.Date, 
                prediction.Time, 
                prediction.PredictionCategory);

            if (existingDict.TryGetValue(key, out var existingRecord))
            {
                // UPDATE SCENARIO: The prediction already exists.
                existingRecord.PredictedOutcome = prediction.PredictedOutcome;
                existingRecord.ConfidenceScore = prediction.ConfidenceScore;
                existingRecord.ExpectedHomeGoals = prediction.ExpectedHomeGoals;
                existingRecord.ExpectedAwayGoals = prediction.ExpectedAwayGoals;
            }
            else
            {
                // INSERT SCENARIO
                _dbContext.RegressionPredictions.Add(prediction);
                
                // Add to dictionary to prevent duplicate inserts if the incoming list 
                // accidentally contains the exact same prediction twice
                existingDict[key] = prediction; 
            }
        }

        // 4. Save all inserts and updates in a single transaction
        await _dbContext.SaveChangesAsync();
    }
    private async Task<PredictionRun> CreatePredictionRunAsync(
        DateOnly targetLocalDate,
        string runReason,
        IReadOnlyCollection<PredictionCandidate> forecastCandidates,
        IReadOnlyCollection<PredictionCandidate> publishedCandidates)
    {
        var localNow = DateTimeProvider.GetLocalTime();
        var predictionRun = new PredictionRun
        {
            TargetLocalDate = targetLocalDate,
            RunKind = "prediction_generation",
            RunLabel = $"{localNow:HH:mm} WAT",
            RunReason = runReason,
            ForecastCount = forecastCandidates.Count,
            PublishedPredictionCount = publishedCandidates.Count,
            StartedAtUtc = DateTime.UtcNow
        };

        await _dbContext.PredictionRuns.AddAsync(predictionRun);
        await _dbContext.SaveChangesAsync();
        return predictionRun;
    }

}
