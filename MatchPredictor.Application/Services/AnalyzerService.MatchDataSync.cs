using System.Globalization;
using System.Text.Json;
using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Statistics;
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
    public async Task ExtractDataAndSyncDatabaseAsync(int predictionDayOffset = 0, string? runReason = null)
    {
        var targetLocalDateTime = DateTimeProvider.GetLocalTime().Date.AddDays(predictionDayOffset);
        var targetLocalDate = DateOnly.FromDateTime(targetLocalDateTime);
        var targetDateString = DateTimeProvider.FormatLocalDate(targetLocalDate);

        _logger.LogInformation(
            "Starting data extraction process for target date {TargetDate} (day offset {PredictionDayOffset}).",
            targetDateString,
            predictionDayOffset);
        try
        {
            await _webScraperService.ScrapeMatchDataAsync();
            _logger.LogInformation("✅ Web scraping for match data completed successfully.");

            var scraped = _excelExtract.ExtractMatchDatasetFromFile(targetLocalDateTime).ToList();
            IReadOnlyList<SourceMarketFixture> sourceMarketFixtures = [];
            try
            {
                sourceMarketFixtures = await _sourceMarketPricingService.GetSourceMarketFixturesForDateAsync(targetLocalDate);
                _logger.LogInformation(
                    "Fetched {Count} source market fixtures for {TargetDate} (market enrichment + odds snapshots).",
                    sourceMarketFixtures.Count,
                    targetDateString);
            }
            catch (Exception sourceMarketEx)
            {
                _logger.LogWarning(sourceMarketEx, "⚠️ Failed to fetch source market fixtures for {TargetDate}. Continuing with workbook-only data.", targetDateString);
            }

            var feedQuality = ExcelFeedQualityValidator.Evaluate(scraped);
            if (feedQuality.Status != ExcelFeedQualityValidator.HealthyStatus)
            {
                _logger.LogWarning(
                    "Sports-ai.dev Excel feed quality {Status}: {Message}",
                    feedQuality.Status,
                    feedQuality.Message);

                if (scraped.Count == 0 && sourceMarketFixtures.Count > 0)
                {
                    scraped = SourceFixtureMatchDataFactory.BuildFromSourceFixtures(sourceMarketFixtures, targetLocalDate);
                    feedQuality = ExcelFeedQualityValidator.Evaluate(scraped);
                    _logger.LogWarning(
                        "Degraded to bookmaker-sourced fixtures ({Count} rows) after Excel feed failure.",
                        scraped.Count);
                }
            }

            await LogScrapingStatus(
                "excel-feed-quality",
                feedQuality.Status == ExcelFeedQualityValidator.FailedStatus ? "Failed" : "Success",
                feedQuality.Message);

            try
            {
                var existingMatches = await _dbContext.MatchDatas
                    .Where(m => m.MatchLocalDate == targetLocalDate)
                    .ToListAsync();

                var existingMatchLookup = existingMatches
                    .GroupBy(m => (
                        FixtureKey: m.FixtureKey ?? string.Empty,
                        Time: m.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(m.Time)))
                    .ToDictionary(group => group.Key, group => group.First());

                foreach (var match in scraped)
                {
                    var canonicalKickoff = DateTimeProvider.ParseCanonicalMatchDateTime(match.Date, match.Time);
                    match.Date = DateTimeProvider.FormatLocalDate(canonicalKickoff.localDate);
                    match.Time = DateTimeProvider.FormatLocalTime(canonicalKickoff.localTime);
                    match.MatchLocalDate = canonicalKickoff.localDate;
                    match.MatchLocalTime = canonicalKickoff.localTime;
                    match.MatchDateTime = canonicalKickoff.utcDateTime;
                    match.FixtureKey = FixtureIdentityFactory.FromMatchData(match).FixtureKey;
                    EnrichSourceMarketProbabilities(match, sourceMarketFixtures);

                    var key = (
                        FixtureKey: match.FixtureKey,
                        Time: match.MatchLocalTime);

                    if (existingMatchLookup.TryGetValue(key, out var existing))
                    {
                        existing.HomeWin = match.HomeWin;
                        existing.Draw = match.Draw;
                        existing.AwayWin = match.AwayWin;
                        existing.OverOneGoal = match.OverOneGoal;
                        existing.OverOnePointFive = match.OverOnePointFive;
                        existing.OverTwoGoals = match.OverTwoGoals;
                        existing.OverThreeGoals = match.OverThreeGoals;
                        existing.OverFourGoals = match.OverFourGoals;
                        existing.UnderOnePointFive = match.UnderOnePointFive;
                        existing.UnderTwoGoals = match.UnderTwoGoals;
                        existing.UnderThreeGoals = match.UnderThreeGoals;
                        existing.AhZeroHome = match.AhZeroHome;
                        existing.AhZeroAway = match.AhZeroAway;
                        existing.AhMinusHalfHome = match.AhMinusHalfHome;
                        existing.AhMinusHalfAway = match.AhMinusHalfAway;
                        existing.AhMinusOneHome = match.AhMinusOneHome;
                        existing.AhMinusOneAway = match.AhMinusOneAway;
                        existing.AhPlusHalfHome = match.AhPlusHalfHome;
                        existing.AhPlusHalfAway = match.AhPlusHalfAway;
                        existing.BttsYes = match.BttsYes;
                        existing.BttsNo = match.BttsNo;
                        existing.MatchLocalDate = match.MatchLocalDate;
                        existing.MatchLocalTime = match.MatchLocalTime;
                        existing.MatchDateTime = match.MatchDateTime;
                        existing.FixtureKey = match.FixtureKey;
                    }
                    else
                    {
                        await _dbContext.MatchDatas.AddAsync(match);
                        existingMatchLookup[key] = match;
                    }
                }
                
                await _dbContext.SaveChangesAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "❌ Failed to save match data to database.");
                throw;
            }
            _logger.LogInformation("Extracted and saved {Count} matches to DB for target date {TargetDate}.", scraped.Count, targetDateString);

            await SaveMarketOddsSnapshotsAsync(scraped, sourceMarketFixtures, targetLocalDate);
            if (_fixtureFeatureService is not null)
            {
                try
                {
                    await _fixtureFeatureService.CaptureFeatureSnapshotsAsync(scraped);
                }
                catch (Exception featureEx)
                {
                    _logger.LogWarning(featureEx, "⚠️ Failed to capture fixture feature snapshots. Continuing with prediction generation.");
                }
            }

            // Chain the next job: Generate predictions only after data is successfully synced
            var normalizedRunReason = string.IsNullOrWhiteSpace(runReason)
                ? (predictionDayOffset > 0 ? "prewarm" : "scheduled-sync")
                : runReason.Trim();
            BackgroundJob.Enqueue<IAnalyzerService>(service => service.GeneratePredictionsAsync(targetDateString, normalizedRunReason));
            _logger.LogInformation("Queued GeneratePredictionsAsync background job for target date {TargetDate}.", targetDateString);
            await LogScrapingStatus(
                DataSyncEventName,
                "Success",
                $"✅ Data sync completed successfully for {targetDateString} ({scraped.Count} matches).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during data scraping and sync.");
            await LogScrapingStatus(DataSyncEventName, "Failed", $"Sync Error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Persists a point-in-time capture of the real bookmaker card for every synced fixture,
    /// de-vigged via Shin (1X2) / Power (two-way). These rows are the honest market feature
    /// for training and backtesting — they are captured before kickoff by construction.
    /// </summary>
    private async Task SaveMarketOddsSnapshotsAsync(
        IReadOnlyList<MatchData> scrapedMatches,
        IReadOnlyList<SourceMarketFixture> sourceMarketFixtures,
        DateOnly targetLocalDate)
    {
        if (sourceMarketFixtures.Count == 0 || scrapedMatches.Count == 0)
        {
            return;
        }

        try
        {
            var nowUtc = DateTime.UtcNow;
            var snapshots = new List<MarketOddsSnapshot>();
            foreach (var match in scrapedMatches)
            {
                if (match.MatchDateTime is { } kickoffUtc && kickoffUtc <= nowUtc)
                {
                    continue; // never capture post-kickoff pricing as a pre-match snapshot
                }

                var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
                    sourceMarketFixtures,
                    match.HomeTeam,
                    match.AwayTeam,
                    match.League,
                    match.MatchDateTime);

                if (sourceFixture is null)
                {
                    continue;
                }

                var fair = SourceMarketDeVig.ToFairProbabilities(sourceFixture);
                if (!fair.HasAnySignal)
                {
                    continue;
                }

                snapshots.Add(new MarketOddsSnapshot
                {
                    FixtureKey = match.FixtureKey,
                    MatchLocalDate = targetLocalDate,
                    MatchDateTimeUtc = match.MatchDateTime,
                    League = match.League?.Trim() ?? string.Empty,
                    HomeTeam = match.HomeTeam?.Trim() ?? string.Empty,
                    AwayTeam = match.AwayTeam?.Trim() ?? string.Empty,
                    SourceName = MarketQuoteResolver.LiveSourceName,
                    HomeWinOdds = sourceFixture.HomeWinOdds,
                    DrawOdds = sourceFixture.DrawOdds,
                    AwayWinOdds = sourceFixture.AwayWinOdds,
                    Over25Odds = sourceFixture.Over25Odds,
                    Under25Odds = sourceFixture.Under25Odds,
                    BttsYesOdds = sourceFixture.BttsYesOdds,
                    BttsNoOdds = sourceFixture.BttsNoOdds,
                    FairHomeWin = fair.HomeWin,
                    FairDraw = fair.Draw,
                    FairAwayWin = fair.AwayWin,
                    FairOver25 = fair.Over25,
                    FairUnder25 = fair.Under25,
                    FairBttsYes = fair.Btts,
                    FairBttsNo = fair.Btts is { } bttsYes ? 1.0 - bttsYes : null,
                    DeVigMethod = SourceMarketDeVig.ResolveMethod(sourceFixture),
                    CapturedAtUtc = nowUtc
                });
            }

            if (snapshots.Count == 0)
            {
                return;
            }

            _dbContext.MarketOddsSnapshots.AddRange(snapshots);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation(
                "Captured {Count} market odds snapshot(s) for {TargetDate}.",
                snapshots.Count,
                DateTimeProvider.FormatLocalDate(targetLocalDate));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Failed to persist market odds snapshots. Continuing — snapshots are non-critical.");
        }
    }

    private void EnrichSourceMarketProbabilities(MatchData match, IReadOnlyList<SourceMarketFixture> sourceMarketFixtures)
    {
        if (sourceMarketFixtures.Count == 0)
            return;

        var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
            sourceMarketFixtures,
            match.HomeTeam,
            match.AwayTeam,
            match.League,
            match.MatchDateTime);

        if (sourceFixture is null)
        {
            return;
        }

        var fair = SourceMarketDeVig.ToFairProbabilities(sourceFixture);

        if (fair.HomeWin is { } homeWin && fair.Draw is { } draw && fair.AwayWin is { } awayWin)
        {
            match.HomeWin = homeWin;
            match.Draw = draw;
            match.AwayWin = awayWin;
        }

        if (fair.Over25 is { } over25 && fair.Under25 is { } under25)
        {
            match.OverTwoGoals = over25;
            match.UnderTwoGoals = under25;
        }

        if (fair.Btts is { } bttsYes)
        {
            match.BttsYes = bttsYes;
            match.BttsNo = 1.0 - bttsYes;
        }
        else if (sourceFixture.BttsYesProbability is { } bttsYesProbability &&
                 sourceFixture.BttsNoProbability is { } bttsNoProbability)
        {
            match.BttsYes = bttsYesProbability;
            match.BttsNo = bttsNoProbability;
        }

        match.NormalizeSourceProbabilities();
    }

}
