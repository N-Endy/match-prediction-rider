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
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
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
            if (predictionDayOffset == 0)
            {
                try
                {
                    sourceMarketFixtures = await _sourceMarketPricingService.GetTodaySourceMarketFixturesAsync();
                    _logger.LogInformation("Fetched {Count} source market fixtures for BTTS enrichment.", sourceMarketFixtures.Count);
                }
                catch (Exception sourceMarketEx)
                {
                    _logger.LogWarning(sourceMarketEx, "⚠️ Failed to fetch source market fixtures for BTTS enrichment. Continuing with workbook-only data.");
                }
            }
            else
            {
                _logger.LogInformation(
                    "Skipping BTTS source market enrichment for target date {TargetDate} because live source pricing is only fetched for the current day.",
                    targetDateString);
            }

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

        if (sourceFixture?.BttsYesProbability is not double bttsYesProbability ||
            sourceFixture.BttsNoProbability is not double bttsNoProbability)
        {
            return;
        }

        match.BttsYes = bttsYesProbability;
        match.BttsNo = bttsNoProbability;
        match.NormalizeSourceProbabilities();
    }

}
