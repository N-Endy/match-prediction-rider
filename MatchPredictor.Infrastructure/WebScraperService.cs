using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Jint;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace MatchPredictor.Infrastructure;

public partial class WebScraperService : IWebScraperService
{
    private static readonly SemaphoreSlim ChromeSessionGate = new(1, 1);
    private static readonly TimeSpan AiScoreBlockedCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChromeSessionAcquireTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChromeDriverCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SofaScoreDiscoveryCacheLifetime = TimeSpan.FromHours(8);
    private const int AiScoreBrowserChallengeProbeSeconds = 6;
    private static readonly TimeSpan SofaScoreSitemapPoolCacheLifetime = TimeSpan.FromHours(2);
    private const int MaxAiScoreNuxtPayloadBytes = 5 * 1024 * 1024;
    private const int MaxAiScoreExtractedJsonBytes = 5 * 1024 * 1024;
    private const long AiScoreJintMemoryLimitBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan AiScoreJintTimeout = TimeSpan.FromSeconds(2);
    private const int AiScoreJintMaxStatements = 25_000;
    private const int DefaultSofaScoreMaxSitemapsPerRun = 48;
    private const int DefaultSofaScoreMaxCandidateUrlsPerFixture = 3;
    private const int DefaultSofaScoreMaxEventPagesPerRun = 72;
    private const int DefaultSofaScoreBrowserScrollRounds = 6;
    private const int DefaultSofaScoreBrowserListingPagesPerRun = 2;
    private static readonly ConcurrentDictionary<string, SofaScoreDiscoveryCacheEntry> SofaScoreEventUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SofaScoreSitemapPoolGate = new();
    private static SofaScoreSitemapPoolCacheEntry? _sofaScoreSitemapPoolCache;
    private readonly string _downloadFolder;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebScraperService> _logger;
    private readonly AiScoreSourceHealthTracker _aiScoreSourceHealthTracker;
    private readonly SofaScoreSourceHealthTracker _sofaScoreSourceHealthTracker;
    private readonly bool _browserScrapingEnabled;

    public WebScraperService(
        IConfiguration configuration,
        ILogger<WebScraperService> logger,
        AiScoreSourceHealthTracker aiScoreSourceHealthTracker,
        SofaScoreSourceHealthTracker sofaScoreSourceHealthTracker)
    {
        _logger = logger;
        _configuration = configuration;
        _aiScoreSourceHealthTracker = aiScoreSourceHealthTracker;
        _sofaScoreSourceHealthTracker = sofaScoreSourceHealthTracker;
        _browserScrapingEnabled = ResolveBrowserScrapingEnabled(configuration);
        
        var baseDirFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
        var currentDirFolder = Path.Combine(Directory.GetCurrentDirectory(), "Resources");
        var parentDirFolder = Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? string.Empty, "Resources");

        if (Directory.Exists(baseDirFolder) || AppDomain.CurrentDomain.BaseDirectory.Contains("publish") || AppDomain.CurrentDomain.BaseDirectory.Contains("bin"))
            _downloadFolder = baseDirFolder;
        else if (Directory.Exists(currentDirFolder))
            _downloadFolder = currentDirFolder;
        else
            _downloadFolder = parentDirFolder;

        Directory.CreateDirectory(_downloadFolder); // Ensure the directory exists
    }
    
    public async Task ScrapeMatchDataAsync()
    {
        try
        {
            DeletePreviousFile();

            var downloadUrl = _configuration["ScrapingValues:PredictionsApiUrl"]
                ?? "https://www.sports-ai.dev/api/generate-excel";
            var fileName = _configuration["ScrapingValues:PredictionsFileName"]
                ?? throw new InvalidOperationException("Predictions file name not configured in appsettings.json");
            var targetPath = Path.Combine(_downloadFolder, fileName);

            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                         System.Net.DecompressionMethods.Deflate |
                                         System.Net.DecompressionMethods.Brotli
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            client.DefaultRequestHeaders.Add(
                "User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add(
                "Accept",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,application/octet-stream;q=0.9,*/*;q=0.8");

            _logger.LogInformation("Downloading predictions workbook from {Url}.", downloadUrl);

            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(targetPath);
            await responseStream.CopyToAsync(fileStream);
            await fileStream.FlushAsync();

            var fileInfo = new FileInfo(targetPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                throw new IOException($"Predictions workbook download from {downloadUrl} produced an empty file.");
            }

            _logger.LogInformation(
                "Predictions workbook downloaded successfully to {Path} ({SizeBytes} bytes).",
                targetPath,
                fileInfo.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while scraping match data.");
            throw;
        }
    }

    public async Task<List<MatchScore>> ScrapeMatchScoresAsync()
    {
        try
        {
            EnsureBrowserScrapingEnabled("FlashScore tennis score scraping");
            var dayOffset = ParseConfiguredSignedInt("ScrapingValues:ScoresDayOffset", 0);
            _logger.LogInformation(
                "Starting FlashScore tennis scrape with day offset {DayOffset}.",
                dayOffset);

            var html = await FetchFlashScoreTennisHtmlViaBrowserAsync(dayOffset);
            _logger.LogInformation(
                "Fetched FlashScore tennis HTML ({HtmlLength} chars). Beginning parse.",
                html.Length);

            var scores = ParseFlashScoreTennisHtml(html, dayOffset);
            _logger.LogInformation(
                "FlashScore tennis parse completed with {RowCount} row(s): {LiveCount} live, {FinishedCount} finished.",
                scores.Count,
                scores.Count(score => score.IsLive),
                scores.Count(score => !score.IsLive));

            return scores;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "❌ An error occurred while scraping match score.");
            throw;
        }
    }

    public async Task<List<MatchScore>> ScrapeTennisScoresMatchScoresAsync()
    {
        try
        {
            using var client = CreateTennisScoresHttpClient();
            var today = DateTimeProvider.GetLocalDate();
            var lookbackDays = Math.Max(ParseConfiguredSignedInt("ScrapingValues:TennisScoresResultsLookbackDays", 2), 0);
            var rows = new List<MatchScore>();
            var baseUrl = ResolveTennisScoresBaseUrl(_configuration["ScrapingValues:TennisScoresWebsite"]);

            _logger.LogInformation(
                "Starting tennisscores.mobi scrape using {BaseUrl} with results lookback of {LookbackDays} day(s).",
                baseUrl,
                lookbackDays);

            var homepageHtml = await FetchTennisScoresHtmlAsync(
                client,
                baseUrl);
            if (!string.IsNullOrWhiteSpace(homepageHtml))
            {
                _logger.LogInformation(
                    "Fetched tennisscores.mobi live homepage HTML ({HtmlLength} chars). Parsing live/finished rows for {TargetDate}.",
                    homepageHtml.Length,
                    today);
                var homepageRows = ParseTennisScoresPageHtml(homepageHtml, today, resultsPage: false);
                rows.AddRange(homepageRows);
                _logger.LogInformation(
                    "Parsed {RowCount} row(s) from the tennisscores.mobi live homepage: {LiveCount} live, {FinishedCount} finished.",
                    homepageRows.Count,
                    homepageRows.Count(score => score.IsLive),
                    homepageRows.Count(score => !score.IsLive));
            }
            else
            {
                _logger.LogWarning("tennisscores.mobi live homepage returned no HTML.");
            }

            for (var dayOffset = 0; dayOffset <= lookbackDays; dayOffset++)
            {
                var targetDate = today.AddDays(-dayOffset);
                var resultsUrl = ResolveTennisScoresResultsUrl(_configuration["ScrapingValues:TennisScoresWebsite"], targetDate);
                _logger.LogInformation(
                    "Fetching tennisscores.mobi results page for {TargetDate} from {ResultsUrl}.",
                    targetDate,
                    resultsUrl);
                var resultsHtml = await FetchTennisScoresHtmlAsync(
                    client,
                    resultsUrl);
                if (string.IsNullOrWhiteSpace(resultsHtml))
                {
                    _logger.LogWarning("tennisscores.mobi results page for {TargetDate} returned no HTML.", targetDate);
                    continue;
                }

                _logger.LogInformation(
                    "Fetched tennisscores.mobi results HTML for {TargetDate} ({HtmlLength} chars). Parsing finished rows.",
                    targetDate,
                    resultsHtml.Length);
                var resultRows = ParseTennisScoresPageHtml(resultsHtml, targetDate, resultsPage: true);
                rows.AddRange(resultRows);
                _logger.LogInformation(
                    "Parsed {RowCount} finished row(s) from tennisscores.mobi results for {TargetDate}.",
                    resultRows.Count,
                    targetDate);
            }

            var deduped = rows
                .GroupBy(BuildTennisScoresRowKey, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(score => score.IsLive ? 1 : 2)
                    .ThenByDescending(score => score.MatchTime)
                    .First())
                .ToList();

            _logger.LogInformation(
                "tennisscores.mobi scrape completed with {RawRowCount} raw row(s) and {DedupedRowCount} deduplicated row(s): {LiveCount} live, {FinishedCount} finished.",
                rows.Count,
                deduped.Count,
                deduped.Count(score => score.IsLive),
                deduped.Count(score => !score.IsLive));

            return deduped;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "❌ An error occurred while scraping tennisscores.mobi tennis scores.");
            throw;
        }
    }

    public async Task<List<AiScoreMatchScore>> ScrapeAiScoreMatchScoresAsync()
    {
        if (_aiScoreSourceHealthTracker.IsInCooldown(DateTime.UtcNow, out var remaining))
        {
            _logger.LogWarning(
                "AiScore is in cooldown for another {RemainingSeconds:0}s after a recent block. Skipping direct fetch and falling back immediately.",
                remaining.TotalSeconds);
            return await FetchAndTrackAiScoreFallbackAsync("AiScore cooldown active.");
        }

        _aiScoreSourceHealthTracker.RecordAttempt("http");

        // ── Primary: HttpClient → extract window.__NUXT__ state from AiScore ──
        try
        {
            var httpAttempt = await ScrapeAiScoreViaHttpAttemptAsync();
            if (httpAttempt.Matches.Count > 0)
            {
                _aiScoreSourceHealthTracker.RecordSuccess("http", httpAttempt.Matches.Count, httpAttempt.Detail);
                _logger.LogInformation("Scraped {Count} match scores from AiScore (HTTP).", httpAttempt.Matches.Count);
                return httpAttempt.Matches;
            }

            if (httpAttempt.Status == AiScoreAttemptStatus.Blocked)
            {
                _aiScoreSourceHealthTracker.RecordHttpBlocked(httpAttempt.Detail);
                _logger.LogWarning("{Detail} Falling back to Headless Browser.", httpAttempt.Detail);
            }
            else
            {
                _aiScoreSourceHealthTracker.RecordEmpty("http", httpAttempt.Detail);
                _logger.LogWarning("AiScore HTTP extraction returned 0 matches. Falling back to Headless Browser.");
            }
        }
        catch (Exception ex)
        {
            _aiScoreSourceHealthTracker.RecordFailure("http", ex.Message);
            _logger.LogWarning(ex, "AiScore HTTP extraction failed. Falling back to Headless Browser.");
        }

        if (!_browserScrapingEnabled)
        {
            var detail = "Browser scraping is disabled. Skipping AiScore headless browser fallback; TennisPredictor has no legacy non-tennis fallback.";
            _aiScoreSourceHealthTracker.RecordAttempt("browser-disabled", detail);
            _logger.LogWarning("{Detail}", detail);
            return await FetchAndTrackAiScoreFallbackAsync("Browser scraping disabled.");
        }

        // ── Secondary: Headless Browser → extract window.__NUXT__ state from AiScore ──
        try
        {
            _aiScoreSourceHealthTracker.RecordAttempt("browser");
            var browserAttempt = await ScrapeAiScoreViaBrowserAttemptAsync();
            if (browserAttempt.Matches.Count > 0)
            {
                _aiScoreSourceHealthTracker.RecordSuccess("browser", browserAttempt.Matches.Count, browserAttempt.Detail);
                _logger.LogInformation("Scraped {Count} match scores from AiScore (Browser).", browserAttempt.Matches.Count);
                return browserAttempt.Matches;
            }

            if (browserAttempt.Status == AiScoreAttemptStatus.Blocked)
            {
                _aiScoreSourceHealthTracker.RecordBrowserBlocked(browserAttempt.Detail, AiScoreBlockedCooldown);
                _logger.LogWarning("{Detail} No additional AiScore fallback is available for TennisPredictor.", browserAttempt.Detail);
            }
            else
            {
                _aiScoreSourceHealthTracker.RecordEmpty("browser", browserAttempt.Detail);
                _logger.LogWarning("AiScore Browser extraction returned 0 matches. No additional AiScore fallback is available for TennisPredictor.");
            }
        }
        catch (Exception ex)
        {
            _aiScoreSourceHealthTracker.RecordFailure("browser", ex.Message);
            _logger.LogWarning(ex, "AiScore Browser extraction failed. No additional AiScore fallback is available for TennisPredictor.");
        }

        return await FetchAndTrackAiScoreFallbackAsync("AiScore unavailable after direct attempts.");
    }

    public async Task<List<SofaScoreMatchScore>> ScrapeSofaScoreMatchScoresAsync(IEnumerable<SofaScoreFixtureRequest> fixtures)
    {
        var requestedFixtures = fixtures
            .Where(fixture => !string.IsNullOrWhiteSpace(fixture.HomeTeam) && !string.IsNullOrWhiteSpace(fixture.AwayTeam))
            .GroupBy(BuildSofaScoreFixtureCacheKey)
            .Select(group => group.First())
            .ToList();

        if (requestedFixtures.Count == 0)
        {
            return [];
        }

        var scores = new List<SofaScoreMatchScore>();

        if (!_browserScrapingEnabled)
        {
            _logger.LogWarning("Browser scraping is disabled. Skipping direct Sofascore browser scrape and trying the HTML fallback path.");

            try
            {
                using var client = CreateSofaScoreHttpClient();
                return await ScrapeSofaScoreViaHtmlFallbackAsync(requestedFixtures, client);
            }
            catch (Exception ex)
            {
                _sofaScoreSourceHealthTracker.RecordFailure("html-fallback", ex.Message);
                _logger.LogWarning(ex, "SofaScore HTML fallback failed while browser scraping was disabled.");
                return scores;
            }
        }

        var capturedTennisApiResponseCount = 0;
        var renderedMatchElementCount = 0;

        try
        {
            var baseUrl = _configuration["ScrapingValues:SofaScoreBaseUrl"] ?? "https://www.sofascore.com";
            var url = baseUrl.TrimEnd('/') + "/tennis";

            _logger.LogInformation("Starting direct Sofascore Selenium scrape at {Url}...", url);

            await RunWithChromeSessionAsync(async driver =>
            {
                EnsureSofaScoreBrowserCaptureInstalled(driver);
                ClearSofaScoreBrowserCapturedResponses(driver);

                driver.Navigate().GoToUrl(url);
                WaitForDocumentReady(driver);
                DismissCookieBanners(driver);

                var waitDeadlineUtc = DateTime.UtcNow.AddSeconds(15);
                var observedAnchorCount = 0;
                var observedTennisApiResponseCount = 0;

                while (DateTime.UtcNow < waitDeadlineUtc)
                {
                    observedAnchorCount = driver.FindElements(By.CssSelector("a[href*='/tennis/match/']")).Count;
                    observedTennisApiResponseCount = ReadSofaScoreBrowserCapturedResponses(driver)
                        .Count(response => response.RelativePath.Contains("/api/v1/sport/tennis/", StringComparison.OrdinalIgnoreCase));

                    if (observedAnchorCount > 0 || observedTennisApiResponseCount > 0)
                    {
                        break;
                    }

                    await Task.Delay(1000);
                }

                await Task.Delay(1000);

                var pageTitle = driver.Title;
                var pageSource = driver.PageSource;
                var capturedResponses = ReadSofaScoreBrowserCapturedResponses(driver);
                var capturedTennisApiResponses = capturedResponses
                    .Where(response => response.RelativePath.Contains("/api/v1/sport/tennis/", StringComparison.OrdinalIgnoreCase))
                    .Select(response => response.RelativePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                capturedTennisApiResponseCount = capturedTennisApiResponses.Count;

                _logger.LogInformation(
                    "Sofascore tennis page diagnostics: title '{Title}', HTML length {HtmlLength}, rendered match anchors {AnchorCount}, captured /api/v1/sport/tennis responses {ApiResponseCount}, any tennis API captured: {HasCapturedResponses}.",
                    pageTitle,
                    pageSource.Length,
                    observedAnchorCount,
                    capturedTennisApiResponses.Count,
                    capturedTennisApiResponses.Count > 0);

                if (capturedTennisApiResponses.Count > 0)
                {
                    _logger.LogInformation(
                        "Sofascore captured tennis API paths: {CapturedPaths}",
                        string.Join(", ", capturedTennisApiResponses.Take(8)));
                }
                else
                {
                    _logger.LogWarning(
                        "Sofascore did not expose any /api/v1/sport/tennis/... responses during the direct browser scrape window.");
                }

                var normalizedBaseUrl = baseUrl.TrimEnd('/');

                var listingEntries = SofaScoreListingPageParser.ParseEntries(pageSource, normalizedBaseUrl);
                renderedMatchElementCount = Math.Max(renderedMatchElementCount, listingEntries.Count);
                _logger.LogInformation(
                    "Sofascore direct listing parser extracted {Count} rendered row(s) from the current page source.",
                    listingEntries.Count);

                var listingScores = ResolveSofaScoreListingScores(requestedFixtures, listingEntries);
                if (listingScores.Count > 0)
                {
                    _logger.LogInformation(
                        "Sofascore direct listing parser matched {Count} targeted fixture(s) from {CandidateCount} rendered row(s).",
                        listingScores.Count,
                        listingEntries.Count);
                    scores.AddRange(listingScores);
                }

                var domEntries = ExtractSofaScoreBrowserDomEntries(driver, normalizedBaseUrl);
                renderedMatchElementCount = Math.Max(renderedMatchElementCount, domEntries.Count);
                _logger.LogInformation(
                    "Sofascore direct rendered-text parser extracted {Count} Selenium row candidate(s).",
                    domEntries.Count);

                var domScores = ResolveSofaScoreListingScores(requestedFixtures, domEntries);
                if (domScores.Count > 0)
                {
                    _logger.LogInformation(
                        "Sofascore direct rendered-text parser matched {Count} targeted fixture(s) from {CandidateCount} Selenium row candidate(s).",
                        domScores.Count,
                        domEntries.Count);

                    foreach (var score in domScores)
                    {
                        if (scores.Any(existing =>
                                TeamsLookEquivalent(existing.HomeTeam, score.HomeTeam) &&
                                TeamsLookEquivalent(existing.AwayTeam, score.AwayTeam) &&
                                string.Equals(existing.Score, score.Score, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        scores.Add(score);
                    }
                }

                if (scores.Count == 0)
                {
                    var matchElements = driver.FindElements(By.XPath("//div[contains(@class, 'EventCell')] | //a[contains(@href, '/tennis/match/')] | //div[contains(@class, 'sc-') and .//div[contains(@direction, 'column')]]"));
                    renderedMatchElementCount = Math.Max(renderedMatchElementCount, matchElements.Count);
                    _logger.LogInformation("Sofascore direct raw fallback found {Count} potential match elements.", matchElements.Count);

                    foreach (var ev in matchElements)
                    {
                        try
                        {
                            var text = ev.Text.Replace("\n", " - ").Trim();
                            if (string.IsNullOrWhiteSpace(text) || text.Length < 5)
                            {
                                continue;
                            }

                            var parsed = ParseSofaScoreTextSelenium(text);
                            if (parsed != null)
                            {
                                scores.Add(parsed);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to parse individual match element from Sofascore raw fallback.");
                        }
                    }

                    _logger.LogInformation("Sofascore direct raw fallback parsed {Count} row(s).", scores.Count);
                }
            }, ConfigureSofaScoreBrowserOptions, "SofaScore direct tennis UI scrape");

            _logger.LogInformation("Successfully scraped {Count} total matches from Sofascore.", scores.Count);
            if (scores.Count == 0)
            {
                _sofaScoreSourceHealthTracker.RecordEmpty(
                    "browser-direct",
                    $"Rendered {renderedMatchElementCount} potential SofaScore element(s) and captured {capturedTennisApiResponseCount} tennis API response path(s), but parsed 0 tennis rows.",
                    capturedTennisApiResponseCount,
                    1);
            }
        }
        catch (Exception ex)
        {
            _sofaScoreSourceHealthTracker.RecordFailure("browser-direct", ex.Message);
            _logger.LogWarning(ex, "SofaScore direct browser scrape failed.");
        }

        // Return only the requested fixtures by doing string similarity/exact match
        var matchedScores = MatchScoresToRequestsSofa(requestedFixtures, scores);

        if (matchedScores.Count > 0)
        {
            _sofaScoreSourceHealthTracker.RecordSuccess(
                "browser-direct",
                matchedScores.Count,
                capturedTennisApiResponseCount,
                1,
                $"Parsed {scores.Count} SofaScore row(s) and matched {matchedScores.Count} of {requestedFixtures.Count} requested fixture(s).");
        }
        else if (scores.Count > 0)
        {
            _sofaScoreSourceHealthTracker.RecordEmpty(
                "browser-direct",
                $"Parsed {scores.Count} SofaScore row(s) but matched 0 of {requestedFixtures.Count} requested fixture(s).",
                capturedTennisApiResponseCount,
                1);
        }

        _logger.LogInformation("Filtered down to {Count} matches corresponding to the requested fixtures.", matchedScores.Count);
        if (matchedScores.Count > 0)
        {
            return matchedScores;
        }

        try
        {
            var browserAttempt = await ScrapeSofaScoreViaBrowserCrawlerAsync(requestedFixtures);
            RecordSofaScoreAttemptResult(browserAttempt);

            if (browserAttempt.Scores.Count > 0)
            {
                _logger.LogInformation(
                    "SofaScore browser crawler matched {Count} targeted fixture score(s) after the direct scrape returned none.",
                    browserAttempt.Scores.Count);
                return browserAttempt.Scores;
            }

            _logger.LogInformation("SofaScore browser crawler returned no targeted rows: {Detail}", browserAttempt.Detail);
        }
        catch (Exception ex)
        {
            _sofaScoreSourceHealthTracker.RecordFailure("browser-crawler", ex.Message);
            _logger.LogWarning(ex, "SofaScore browser crawler fallback failed.");
        }

        try
        {
            using var client = CreateSofaScoreHttpClient();
            var htmlFallbackScores = await ScrapeSofaScoreViaHtmlFallbackAsync(requestedFixtures, client);
            if (htmlFallbackScores.Count > 0)
            {
                _logger.LogInformation(
                    "SofaScore HTML fallback matched {Count} targeted fixture score(s) after browser attempts returned none.",
                    htmlFallbackScores.Count);
                return htmlFallbackScores;
            }
        }
        catch (Exception ex)
        {
            _sofaScoreSourceHealthTracker.RecordFailure("html-fallback", ex.Message);
            _logger.LogWarning(ex, "SofaScore HTML fallback failed.");
        }

        return matchedScores;
    }

    private SofaScoreMatchScore? ParseSofaScoreTextSelenium(string text)
    {
        var tokens = text
            .Split(new[] { " - " }, StringSplitOptions.None)
            .Select(NormalizeTennisScoresText)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
        if (tokens.Count < 3)
        {
            return null;
        }

        var tokenIndex = 0;
        var matchTime = DateTime.UtcNow;
        var statusText = string.Empty;

        if (TryParseSofaScoreLeadingToken(tokens[0], out var parsedMatchTime, out var leadingStatus))
        {
            matchTime = parsedMatchTime;
            statusText = leadingStatus;
            tokenIndex = 1;
        }
        else if (LooksLikeSofaScoreStatusToken(tokens[0]))
        {
            statusText = tokens[0];
            tokenIndex = 1;
        }

        if (tokens.Count - tokenIndex >= 3 &&
            string.IsNullOrWhiteSpace(statusText) &&
            LooksLikeSofaScoreStatusToken(tokens[tokenIndex]))
        {
            statusText = tokens[tokenIndex];
            tokenIndex++;
        }

        if (tokens.Count - tokenIndex < 2)
        {
            return null;
        }

        var homeTeam = tokens[tokenIndex];
        var awayTeam = tokens[tokenIndex + 1];
        if (string.IsNullOrWhiteSpace(homeTeam) || string.IsNullOrWhiteSpace(awayTeam))
        {
            return null;
        }

        var isLive = LooksLikeSofaScoreLiveStatus(statusText);
        string? normalizedScoreline = null;
        int? homeSetsWon = null;
        int? awaySetsWon = null;

        foreach (var token in tokens.Skip(tokenIndex + 2))
        {
            if (TryParseSofaScoreScoreToken(token, out var parsedHomeSets, out var parsedAwaySets, out var parsedScore))
            {
                homeSetsWon = parsedHomeSets;
                awaySetsWon = parsedAwaySets;
                normalizedScoreline = parsedScore;
                break;
            }
        }

        if (!homeSetsWon.HasValue || !awaySetsWon.HasValue)
        {
            return null;
        }

        return new SofaScoreMatchScore
        {
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            Score = normalizedScoreline ?? $"{homeSetsWon.Value}:{awaySetsWon.Value}",
            NormalizedScoreline = normalizedScoreline,
            HomeSetsWon = homeSetsWon,
            AwaySetsWon = awaySetsWon,
            StatusText = string.IsNullOrWhiteSpace(statusText) ? null : statusText,
            MatchTime = matchTime,
            IsLive = isLive,
            League = "Unknown"
        };
    }

    private List<SofaScoreMatchScore> MatchScoresToRequestsSofa(List<SofaScoreFixtureRequest> requests, List<SofaScoreMatchScore> scraped)
    {
        var results = new List<SofaScoreMatchScore>();
        var remainingScores = scraped.ToList();

        foreach (var req in requests)
        {
            var bestMatch = remainingScores
                .Select(score => new
                {
                    Score = ScoreDirectSofaScoreCandidate(req, score),
                    Match = score
                })
                .Where(entry => entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => Math.Abs(GetKickoffDeltaMinutes(entry.Match.MatchTime, req.ScheduledMatchTimeUtc)))
                .Select(entry => entry.Match)
                .FirstOrDefault();

            if (bestMatch != null)
            {
                results.Add(bestMatch);
                remainingScores.Remove(bestMatch);
            }
        }

        return results;
    }

    private int ScoreDirectSofaScoreCandidate(SofaScoreFixtureRequest fixture, SofaScoreMatchScore candidate)
    {
        if (!TeamsLookEquivalent(candidate.HomeTeam, fixture.HomeTeam) ||
            !TeamsLookEquivalent(candidate.AwayTeam, fixture.AwayTeam))
        {
            return 0;
        }

        var score = 0;

        score += string.Equals(
            NormalizeFixtureKeyPart(candidate.HomeTeam),
            NormalizeFixtureKeyPart(fixture.HomeTeam),
            StringComparison.OrdinalIgnoreCase)
            ? 40
            : 24;

        score += string.Equals(
            NormalizeFixtureKeyPart(candidate.AwayTeam),
            NormalizeFixtureKeyPart(fixture.AwayTeam),
            StringComparison.OrdinalIgnoreCase)
            ? 40
            : 24;

        var candidateLocalDate = DateTimeProvider.ConvertUtcToLocalDate(candidate.MatchTime);
        if (candidateLocalDate == fixture.MatchLocalDate)
        {
            score += 15;
        }
        else
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(candidate.League) &&
            !string.IsNullOrWhiteSpace(fixture.League) &&
            TeamsLookEquivalent(candidate.League, fixture.League))
        {
            score += 10;
        }

        var kickoffDeltaMinutes = Math.Abs(GetKickoffDeltaMinutes(candidate.MatchTime, fixture.ScheduledMatchTimeUtc));
        if (kickoffDeltaMinutes <= 15)
        {
            score += 20;
        }
        else if (kickoffDeltaMinutes <= 90)
        {
            score += 10;
        }
        else if (kickoffDeltaMinutes <= 240)
        {
            score += 4;
        }

        if (!candidate.IsLive)
        {
            score += 5;
        }

        return score >= 70 ? score : 0;
    }

    private void RecordSofaScoreAttemptResult(SofaScoreApiAttemptResult attempt)
    {
        if (attempt.Scores.Count > 0)
        {
            _sofaScoreSourceHealthTracker.RecordSuccess(
                attempt.Stage,
                attempt.Scores.Count,
                attempt.CandidateCount,
                attempt.DetailFetchCount,
                attempt.Detail);
            return;
        }

        if (attempt.Detail.Contains("challenge page", StringComparison.OrdinalIgnoreCase) ||
            attempt.Detail.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            _sofaScoreSourceHealthTracker.RecordBlocked(attempt.Stage, attempt.Detail);
            return;
        }

        _sofaScoreSourceHealthTracker.RecordEmpty(
            attempt.Stage,
            attempt.Detail,
            attempt.CandidateCount,
            attempt.DetailFetchCount);
    }

    /// <summary>
    /// Extracts match scores from AiScore by downloading the HTML via HttpClient.
    /// Fast, but might get blocked by Cloudflare (403 Forbidden).
    /// </summary>
    private async Task<AiScoreAttemptResult> ScrapeAiScoreViaHttpAttemptAsync()
    {
        var aiScoreUrl = ResolveAiScoreTennisUrl(_configuration["ScrapingValues:AiScoreWebsite"]);

        _logger.LogInformation("Fetching AiScore SSR HTML via HTTP...");

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
        };
        using var client = new HttpClient(handler);
        
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
        client.Timeout = TimeSpan.FromSeconds(30);

        var response = await client.GetAsync(aiScoreUrl);
        if (!response.IsSuccessStatusCode)
        {
            var detail = $"AiScore HTTP returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            if ((int)response.StatusCode is 403 or 429)
            {
                return new AiScoreAttemptResult([], AiScoreAttemptStatus.Blocked, detail);
            }

            _logger.LogWarning("AiScore HTTP returned {StatusCode} {ReasonPhrase}.", response.StatusCode, response.ReasonPhrase);
            return new AiScoreAttemptResult([], AiScoreAttemptStatus.Empty, detail);
        }

        var html = await response.Content.ReadAsStringAsync();
        if (LooksLikeAiScoreChallengePage(html))
        {
            return new AiScoreAttemptResult([], AiScoreAttemptStatus.Blocked, "AiScore HTTP returned a challenge page instead of score data.");
        }

        var matches = ParseAiScoreNuxtState(html);
        return matches.Count > 0
            ? new AiScoreAttemptResult(matches, AiScoreAttemptStatus.Success, $"Fetched {matches.Count} match(es) from AiScore HTTP.")
            : new AiScoreAttemptResult([], AiScoreAttemptStatus.Empty, "AiScore HTTP returned HTML without extractable match data.");
    }

    /// <summary>
    /// Extracts match scores from AiScore by loading the page via Headless Browser
    /// and extracting JSON data from window.__NUXT__ using the JS executor.
    /// </summary>
    private async Task<AiScoreAttemptResult> ScrapeAiScoreViaBrowserAttemptAsync()
    {
        var aiScoreUrl = ResolveAiScoreTennisUrl(_configuration["ScrapingValues:AiScoreWebsite"]);

        _logger.LogInformation("Fetching AiScore SSR HTML via Headless Browser...");

        try
        {
            return await RunWithChromeSessionAsync(
                async driver =>
                {
                    var js = (IJavaScriptExecutor)driver;
                    js.ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

                    await driver.Navigate().GoToUrlAsync(aiScoreUrl);

                    var elapsed = 0;
                    while (elapsed < AiScoreBrowserChallengeProbeSeconds)
                    {
                        await Task.Delay(1000);
                        elapsed += 1;
                        var src = driver.PageSource;
                        var title = driver.Title;
                        if (!LooksLikeAiScoreChallengePage(src, title))
                        {
                            _logger.LogInformation("AiScore browser challenge cleared after {Elapsed}s.", elapsed);
                            break;
                        }

                        _logger.LogDebug("Still waiting for AiScore challenge to clear at {Elapsed}s...", elapsed);
                    }

                    var currentHtml = driver.PageSource;
                    var currentTitle = driver.Title;
                    if (LooksLikeAiScoreChallengePage(currentHtml, currentTitle))
                    {
                        _logger.LogWarning(
                            "AiScore Browser: challenge page detected after {Elapsed}s. Title: '{Title}', HTML length: {Len}.",
                            elapsed,
                            currentTitle,
                            currentHtml.Length);
                        return new AiScoreAttemptResult([], AiScoreAttemptStatus.Blocked, $"AiScore browser was blocked by a challenge page ('{currentTitle}').");
                    }

                    await Task.Delay(2000);

                    // --- Strategy 1: Extract __NUXT__ via JavaScript executor (preferred) ---
                    var hasNuxt = js.ExecuteScript("return !!window.__NUXT__;") is true;
                    if (hasNuxt)
                    {
                        _logger.LogInformation("Found window.__NUXT__ via JS executor.");
                    var nuxtJson = js.ExecuteScript(@"
                    var s = window.__NUXT__ && window.__NUXT__.state && window.__NUXT__.state['tennis/home'];
                    if (!s) return JSON.stringify({matches:[], teams:[], comps:[]});
                    return JSON.stringify({ 
                        matches: s.matchesData_matches || [], 
                        teams: s.matchesData_teams || [], 
                        comps: s.matchesData_competitions || [] 
                    });
                        ")?.ToString();
                        if (string.IsNullOrWhiteSpace(nuxtJson))
                        {
                            return new AiScoreAttemptResult([], AiScoreAttemptStatus.Empty, "AiScore browser found hydrated state, but the extracted JSON payload was empty.");
                        }

                        var matches = ParseAiScoreExtractedJson(nuxtJson);
                        return matches.Count > 0
                            ? new AiScoreAttemptResult(matches, AiScoreAttemptStatus.Success, $"Fetched {matches.Count} match(es) from AiScore browser extraction.")
                            : new AiScoreAttemptResult([], AiScoreAttemptStatus.Empty, "AiScore browser found hydrated state, but no match rows were extracted.");
                    }

                    // --- Strategy 2: Extract __NEXT_DATA__ via JS executor ---
                    var hasNext = js.ExecuteScript("return !!window.__NEXT_DATA__;") is true;
                    if (hasNext)
                    {
                        _logger.LogInformation("Found window.__NEXT_DATA__ via JS executor.");
                        var nextJson = js.ExecuteScript("return JSON.stringify(window.__NEXT_DATA__);")?.ToString();
                        _logger.LogDebug("NEXT_DATA preview: {Preview}", nextJson?.Substring(0, Math.Min(nextJson.Length, 300)));
                        // For now, log and fall through — parse if structure is known
                    }

                    // --- Strategy 3: Regex on page source (legacy fallback) ---
                    var html = driver.PageSource;
                    var result = ParseAiScoreNuxtState(html);
                    if (result.Count > 0)
                    {
                        return new AiScoreAttemptResult(result, AiScoreAttemptStatus.Success, $"Fetched {result.Count} match(es) from AiScore browser HTML.");
                    }

                    // Debugging: log what the page actually contains
                    var pageTitle = driver.Title;
                    var pageLen = html.Length;
                    _logger.LogWarning("AiScore Browser: No data extracted. Page title: '{Title}', HTML length: {Len}, first 300 chars: {Preview}",
                        pageTitle, pageLen, html.Substring(0, Math.Min(pageLen, 300)));

                    return new AiScoreAttemptResult([], AiScoreAttemptStatus.Empty, $"AiScore browser loaded '{pageTitle}' but no extractable match data was found.");
                },
                ConfigureAiScoreBrowserOptions,
                "AiScore browser scrape");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load AiScore via Headless Chrome.");
            return new AiScoreAttemptResult([], AiScoreAttemptStatus.Failed, $"AiScore browser failure: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the pre-extracted JSON from the JS executor (already a clean JSON string).
    /// </summary>
    private List<AiScoreMatchScore> ParseAiScoreExtractedJson(string extractedJson)
    {
        var matchScores = new List<AiScoreMatchScore>();

        try
        {
            var payloadBytes = Encoding.UTF8.GetByteCount(extractedJson);
            if (payloadBytes > MaxAiScoreExtractedJsonBytes)
            {
                _logger.LogWarning(
                    "Skipping AiScore extracted JSON parse because the payload size {PayloadBytes} bytes exceeded the safe limit of {LimitBytes} bytes.",
                    payloadBytes,
                    MaxAiScoreExtractedJsonBytes);
                return matchScores;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(extractedJson);
            var root = doc.RootElement;
            var matchesArr = root.GetProperty("matches");
            var teamsArr = root.GetProperty("teams");
            var compsArr = root.GetProperty("comps");

            var teamsDict = new Dictionary<string, string>();
            foreach (var t in teamsArr.EnumerateArray())
            {
                var id = t.GetProperty("id").GetString();
                var name = t.TryGetProperty("name", out var n) ? n.GetString() : t.TryGetProperty("n", out var nn) ? nn.GetString() : "";
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) teamsDict[id] = name;
            }

            var compsDict = new Dictionary<string, string>();
            foreach (var c in compsArr.EnumerateArray())
            {
                var id = c.GetProperty("id").GetString();
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : c.TryGetProperty("n", out var nn) ? nn.GetString() : "";
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) compsDict[id] = name;
            }

            foreach (var m in matchesArr.EnumerateArray())
            {
                try
                {
                    var sid = m.TryGetProperty("statusId", out var sidProp) ? sidProp.GetInt32() : 0;
                    var isLive = (sid >= 2 && sid <= 7);
                    var isFinished = (sid >= 8 && sid <= 10);
                    if (!isLive && !isFinished) continue;

                    var homeScores = m.TryGetProperty("homeScores", out var hs) ? hs : default;
                    var awayScores = m.TryGetProperty("awayScores", out var ascv) ? ascv : default;
                    if (homeScores.ValueKind != System.Text.Json.JsonValueKind.Array ||
                        awayScores.ValueKind != System.Text.Json.JsonValueKind.Array ||
                        homeScores.GetArrayLength() == 0 || awayScores.GetArrayLength() == 0)
                        continue;

                    var homeSetsWon = homeScores[0].GetInt32();
                    var awaySetsWon = awayScores[0].GetInt32();

                    var htId = m.TryGetProperty("homeTeam", out var ht) ? ht.GetProperty("id").GetString() : m.TryGetProperty("homeTeamId", out var hti) ? hti.GetString() : "";
                    var atId = m.TryGetProperty("awayTeam", out var at) ? at.GetProperty("id").GetString() : m.TryGetProperty("awayTeamId", out var ati) ? ati.GetString() : "";
                    var cId = m.TryGetProperty("competition", out var comp) ? comp.GetProperty("id").GetString() : m.TryGetProperty("competitionId", out var ci) ? ci.GetString() : "";

                    var homeName = !string.IsNullOrEmpty(htId) && teamsDict.TryGetValue(htId, out var hn) ? hn : "";
                    var awayName = !string.IsNullOrEmpty(atId) && teamsDict.TryGetValue(atId, out var an) ? an : "";
                    var leagueName = !string.IsNullOrEmpty(cId) && compsDict.TryGetValue(cId, out var ln) ? ln : "";

                    var matchTimeUnix = m.TryGetProperty("matchTime", out var mt) ? mt.GetInt64() : 0;
                    var matchTime = matchTimeUnix > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(matchTimeUnix).UtcDateTime
                        : DateTime.UtcNow;

                    var score = $"{homeSetsWon}:{awaySetsWon}";
                    var scoreSummary = BuildSetScoreSummary(score);
                    matchScores.Add(new AiScoreMatchScore
                    {
                        League = leagueName,
                        HomeTeam = homeName,
                        AwayTeam = awayName,
                        Score = score,
                        NormalizedScoreline = scoreSummary.NormalizedScoreline,
                        HomeSetsWon = scoreSummary.HomeSetsWon,
                        AwaySetsWon = scoreSummary.AwaySetsWon,
                        MatchTime = matchTime,
                        IsLive = isLive
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping AiScore match due to parse error.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse AiScore extracted JSON.");
        }

        return matchScores;
    }

    private List<AiScoreMatchScore> ParseAiScoreNuxtState(string html)
    {
        var matchScores = new List<AiScoreMatchScore>();

        // Nuxt data injection: window.__NUXT__=(...);</script>
        var match = Regex.Match(html, @"window\.__NUXT__=(.*?);</script>");
        if (!match.Success)
        {
            _logger.LogWarning("AiScore Nuxt extraction error: No __NUXT__ match in HTML.");
            return matchScores;
        }

        var jsonStr = match.Groups[1].Value;
        
        try
        {
            var payloadBytes = Encoding.UTF8.GetByteCount(jsonStr);
            if (payloadBytes > MaxAiScoreNuxtPayloadBytes)
            {
                _logger.LogWarning(
                    "Skipping AiScore Nuxt parsing because the payload size {PayloadBytes} bytes exceeded the safe limit of {LimitBytes} bytes.",
                    payloadBytes,
                    MaxAiScoreNuxtPayloadBytes);
                return matchScores;
            }

            var engine = new Engine(options => options
                .TimeoutInterval(AiScoreJintTimeout)
                .LimitMemory(AiScoreJintMemoryLimitBytes)
                .MaxStatements(AiScoreJintMaxStatements));
            engine.Execute("var nuxt = " + jsonStr);
            var extractedJson = engine.Evaluate(@"
                JSON.stringify({ 
                    matches: (nuxt.state['tennis/home'] || {}).matchesData_matches || [], 
                    teams: (nuxt.state['tennis/home'] || {}).matchesData_teams || [], 
                    comps: (nuxt.state['tennis/home'] || {}).matchesData_competitions || [] 
                })
            ").AsString();

            using var doc = System.Text.Json.JsonDocument.Parse(extractedJson);
            var root = doc.RootElement;
            var matchesArr = root.GetProperty("matches");
            var teamsArr = root.GetProperty("teams");
            var compsArr = root.GetProperty("comps");

            // Build dictionaries for constant O(1) lookups
            var teamsDict = new Dictionary<string, string>();
            foreach (var t in teamsArr.EnumerateArray())
            {
                var id = t.GetProperty("id").GetString();
                var name = t.TryGetProperty("name", out var n) ? n.GetString() : t.TryGetProperty("n", out var nn) ? nn.GetString() : "";
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) teamsDict[id] = name;
            }

            var compsDict = new Dictionary<string, string>();
            foreach (var c in compsArr.EnumerateArray())
            {
                var id = c.GetProperty("id").GetString();
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : c.TryGetProperty("n", out var nn) ? nn.GetString() : "";
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) compsDict[id] = name;
            }

            foreach (var m in matchesArr.EnumerateArray())
            {
                try
                {
                    var sid = m.TryGetProperty("statusId", out var sidProp) ? sidProp.GetInt32() : 0;
                    var isLive = (sid >= 2 && sid <= 7);
                    var isFinished = (sid >= 8 && sid <= 10);

                    if (!isLive && !isFinished) continue;

                    var homeScores = m.TryGetProperty("homeScores", out var hs) ? hs : default;
                    var awayScores = m.TryGetProperty("awayScores", out var ascv) ? ascv : default;

                    if (homeScores.ValueKind != System.Text.Json.JsonValueKind.Array || 
                        awayScores.ValueKind != System.Text.Json.JsonValueKind.Array ||
                        homeScores.GetArrayLength() == 0 ||
                        awayScores.GetArrayLength() == 0) 
                        continue;

                    var homeSetsWon = homeScores[0].GetInt32();
                    var awaySetsWon = awayScores[0].GetInt32();
                    
                    var htId = m.TryGetProperty("homeTeam", out var ht) ? ht.GetProperty("id").GetString() : m.TryGetProperty("homeTeamId", out var hti) ? hti.GetString() : "";
                    var atId = m.TryGetProperty("awayTeam", out var at) ? at.GetProperty("id").GetString() : m.TryGetProperty("awayTeamId", out var ati) ? ati.GetString() : "";
                    var cId = m.TryGetProperty("competition", out var comp) ? comp.GetProperty("id").GetString() : m.TryGetProperty("competitionId", out var ci) ? ci.GetString() : "";

                    var homeName = !string.IsNullOrEmpty(htId) && teamsDict.TryGetValue(htId, out var hn) ? hn : "";
                    var awayName = !string.IsNullOrEmpty(atId) && teamsDict.TryGetValue(atId, out var an) ? an : "";
                    var leagueName = !string.IsNullOrEmpty(cId) && compsDict.TryGetValue(cId, out var ln) ? ln : "";

                    var matchTimeUnix = m.TryGetProperty("matchTime", out var mt) ? mt.GetInt64() : 0;
                    var matchTime = matchTimeUnix > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(matchTimeUnix).UtcDateTime
                        : DateTime.UtcNow;

                    var score = $"{homeSetsWon}:{awaySetsWon}";
                    var scoreSummary = BuildSetScoreSummary(score);

                    matchScores.Add(new AiScoreMatchScore
                    {
                        League = leagueName,
                        HomeTeam = homeName,
                        AwayTeam = awayName,
                        Score = score,
                        NormalizedScoreline = scoreSummary.NormalizedScoreline,
                        HomeSetsWon = scoreSummary.HomeSetsWon,
                        AwaySetsWon = scoreSummary.AwaySetsWon,
                        MatchTime = matchTime,
                        IsLive = isLive
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping AiScore Browser match due to parse error.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jint extraction/parsing failed for AiScore Nuxt state.");
        }

        return matchScores;
    }

    private Task<List<AiScoreMatchScore>> FetchAndTrackAiScoreFallbackAsync(string reason)
    {
        _aiScoreSourceHealthTracker.RecordEmpty(
            "legacy-fallback-disabled",
            $"{reason} Legacy non-tennis fallback is disabled for TennisPredictor.");
        _logger.LogWarning("AiScore direct sources were unavailable. The legacy non-tennis fallback is disabled for TennisPredictor.");
        return Task.FromResult(new List<AiScoreMatchScore>());
    }

    private static string NormalizeFixtureKeyPart(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string BuildSofaScoreFixtureCacheKey(SofaScoreFixtureRequest fixture)
    {
        return string.Join(
            "|",
            fixture.MatchLocalDate.ToString("yyyy-MM-dd"),
            NormalizeFixtureKeyPart(fixture.HomeTeam),
            NormalizeFixtureKeyPart(fixture.AwayTeam));
    }

    private HttpClient CreateSofaScoreHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json,text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Referrer = new Uri((_configuration["ScrapingValues:SofaScoreBaseUrl"] ?? "https://www.sofascore.com").TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private async Task<SofaScoreApiAttemptResult> ScrapeSofaScoreViaBrowserCrawlerAsync(IReadOnlyList<SofaScoreFixtureRequest> fixtures)
    {
        return await RunWithChromeSessionAsync(
            async driver =>
            {
                EnsureSofaScoreBrowserCaptureInstalled(driver);
                var listingUrls = BuildSofaScoreBrowserListingUrls();
                var listingAttempt = await TryFetchSofaScoreScoresViaBrowserSessionAsync(driver, fixtures, listingUrls);
                if (listingAttempt.Scores.Count > 0)
                {
                    return listingAttempt;
                }

                var nowUtc = DateTime.UtcNow;
                var eventUrlsByFixture = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var unresolvedFixtures = new List<SofaScoreFixtureRequest>();

                foreach (var fixture in fixtures)
                {
                    var cacheKey = BuildSofaScoreFixtureCacheKey(fixture);
                    if (SofaScoreEventUrlCache.TryGetValue(cacheKey, out var cached) &&
                        cached.ExpiresAtUtc > nowUtc &&
                        !string.IsNullOrWhiteSpace(cached.EventUrl))
                    {
                        eventUrlsByFixture[cacheKey] = [cached.EventUrl];
                        continue;
                    }

                    unresolvedFixtures.Add(fixture);
                }

                var candidateUrlCount = eventUrlsByFixture.Values.Sum(urls => urls.Count);
                if (unresolvedFixtures.Count == 0)
                {
                    return await FetchSofaScoreBrowserEventPagesAsync(fixtures, eventUrlsByFixture);
                }

                var discoveredUrls = await CollectSofaScoreCandidateUrlsViaBrowserAsync(driver, listingUrls);

                if (discoveredUrls.Count == 0)
                {
                    return new SofaScoreApiAttemptResult(
                        [],
                        listingAttempt.Stage,
                        listingAttempt.Detail,
                        candidateUrlCount,
                        0);
                }

                var maxCandidateUrlsPerFixture = ParseConfiguredInt("ScrapingValues:SofaScoreMaxCandidateUrlsPerFixture", DefaultSofaScoreMaxCandidateUrlsPerFixture);
                foreach (var fixture in unresolvedFixtures)
                {
                    var cacheKey = BuildSofaScoreFixtureCacheKey(fixture);
                    var homeSlugs = SofaScoreDiscoveryHelper.BuildSlugCandidates(fixture.HomeTeam);
                    var awaySlugs = SofaScoreDiscoveryHelper.BuildSlugCandidates(fixture.AwayTeam);

                    var matches = discoveredUrls
                        .Select(url => new
                        {
                            Url = url,
                            Score = SofaScoreDiscoveryHelper.ScoreUrlAgainstFixture(url, homeSlugs, awaySlugs)
                        })
                        .Where(candidate => candidate.Score > 0)
                        .OrderByDescending(candidate => candidate.Score)
                        .ThenBy(candidate => candidate.Url)
                        .Take(maxCandidateUrlsPerFixture)
                        .Select(candidate => candidate.Url)
                        .ToList();

                    if (matches.Count == 0)
                    {
                        continue;
                    }

                    eventUrlsByFixture[cacheKey] = matches;
                    candidateUrlCount += matches.Count;
                    SofaScoreEventUrlCache[cacheKey] = new SofaScoreDiscoveryCacheEntry(matches[0], nowUtc.Add(SofaScoreDiscoveryCacheLifetime));
                }

                if (eventUrlsByFixture.Count == 0)
                {
                    return new SofaScoreApiAttemptResult(
                        [],
                        "browser-discovery",
                        $"SofaScore browser single-page extraction and URL discovery found no targeted fixtures across {listingUrls.Count} rendered listing page(s).",
                        discoveredUrls.Count,
                        0);
                }

                return await FetchSofaScoreBrowserEventPagesAsync(fixtures, eventUrlsByFixture, driver);
            },
            ConfigureSofaScoreBrowserOptions,
            "SofaScore browser crawl");
    }

    private async Task<SofaScoreApiAttemptResult> TryFetchSofaScoreScoresViaBrowserSessionAsync(
        ChromeDriver driver,
        IReadOnlyList<SofaScoreFixtureRequest> fixtures,
        IReadOnlyList<string> listingUrls)
    {
        var listingPageBudget = ParseConfiguredInt("ScrapingValues:SofaScoreBrowserListingPagesPerRun", DefaultSofaScoreBrowserListingPagesPerRun);
        SofaScoreApiAttemptResult? lastAttempt = null;

        foreach (var listingUrl in listingUrls.Take(listingPageBudget))
        {
            var attempt = await TryFetchSofaScoreScoresViaBrowserListingAsync(driver, fixtures, listingUrl);
            if (attempt.Scores.Count > 0)
            {
                return attempt;
            }

            lastAttempt = attempt;

            if (attempt.Detail.Contains("challenge page", StringComparison.OrdinalIgnoreCase))
            {
                return attempt;
            }
        }

        return lastAttempt ?? new SofaScoreApiAttemptResult(
            [],
            "browser-listing",
            $"SofaScore browser single-page extraction had no listing URL to process for {fixtures.Count} targeted fixtures.",
            0,
            0);
    }

    private async Task<SofaScoreApiAttemptResult> TryFetchSofaScoreScoresViaBrowserListingAsync(
        ChromeDriver driver,
        IReadOnlyList<SofaScoreFixtureRequest> fixtures,
        string listingUrl)
    {
        try
        {
            ClearSofaScoreBrowserCapturedResponses(driver);
            await driver.Navigate().GoToUrlAsync(listingUrl);
            WaitForDocumentReady(driver);
            DismissCookieBanners(driver);
            await Task.Delay(1800);

            var title = driver.Title;
            var pageSource = driver.PageSource;
            if (LooksLikeSofaScoreChallengePage(pageSource, title))
            {
                return new SofaScoreApiAttemptResult(
                    [],
                    "browser-listing",
                    $"SofaScore browser listing page was blocked by a challenge page ('{title}').",
                    0,
                    0);
            }

            var baseUrl = (_configuration["ScrapingValues:SofaScoreBaseUrl"] ?? "https://www.sofascore.com").TrimEnd('/');
            var apiAttempt = TryFetchSofaScoreScoresViaBrowserApiResponses(driver, fixtures, baseUrl, listingUrl);
            if (apiAttempt.Scores.Count > 0)
            {
                return apiAttempt;
            }

            var listingEntries = SofaScoreListingPageParser.ParseEntries(pageSource, baseUrl);
            var listingScores = ResolveSofaScoreListingScores(fixtures, listingEntries);

            if (listingScores.Count > 0)
            {
                return BuildSofaScoreListingAttempt(
                    listingScores,
                    listingEntries.Count,
                    $"SofaScore browser listing DOM matched {listingScores.Count} targeted fixture(s) from {listingEntries.Count} rendered row(s).");
            }

            var domEntries = ExtractSofaScoreBrowserDomEntries(driver, baseUrl);
            var domScores = ResolveSofaScoreListingScores(fixtures, domEntries);
            if (domScores.Count > 0)
            {
                return BuildSofaScoreListingAttempt(
                    domScores,
                    domEntries.Count,
                    $"SofaScore browser rendered text matched {domScores.Count} targeted fixture(s) from {domEntries.Count} Selenium row candidate(s).");
            }

            return new SofaScoreApiAttemptResult(
                [],
                domEntries.Count > 0 ? "browser-listing-dom-text" : "browser-listing-dom",
                domEntries.Count > 0
                    ? $"SofaScore browser rendered text parsed {domEntries.Count} Selenium row candidate(s) from {listingUrl}, but none matched the targeted fixtures."
                    : listingEntries.Count > 0
                        ? $"SofaScore browser listing DOM parsed {listingEntries.Count} rendered row(s) from {listingUrl}, but none matched the targeted fixtures."
                        : apiAttempt.CandidateCount > 0
                            ? apiAttempt.Detail
                            : $"SofaScore browser listing DOM found no rendered match rows on {listingUrl}.",
                Math.Max(Math.Max(listingEntries.Count, domEntries.Count), apiAttempt.CandidateCount),
                0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SofaScore browser single-page extraction failed for {Url}.", listingUrl);
            return new SofaScoreApiAttemptResult(
                [],
                "browser-listing",
                $"SofaScore browser single-page extraction failed for {listingUrl}: {ex.Message}",
                0,
                0);
        }
    }

    private SofaScoreApiAttemptResult BuildSofaScoreListingAttempt(
        IReadOnlyList<SofaScoreMatchScore> scores,
        int candidateCount,
        string detail)
    {
        return new SofaScoreApiAttemptResult(
            scores.ToList(),
            "browser-listing-dom",
            detail,
            candidateCount,
            0);
    }

    private SofaScoreApiAttemptResult TryFetchSofaScoreScoresViaBrowserApiResponses(
        ChromeDriver driver,
        IReadOnlyList<SofaScoreFixtureRequest> fixtures,
        string baseUrl,
        string listingUrl)
    {
        var responses = ReadSofaScoreBrowserCapturedResponses(driver);
        if (responses.Count == 0)
        {
            return new SofaScoreApiAttemptResult(
                [],
                "browser-listing-api",
                $"SofaScore browser capture observed no relevant API responses on {listingUrl}.",
                0,
                0);
        }

        SofaScoreBrowserFetchParser.ParseEventSummaries(
            responses,
            baseUrl,
            out var liveEvents,
            out var scheduledEvents);

        var summaries = liveEvents
            .Concat(scheduledEvents)
            .GroupBy(summary => summary.EventId)
            .Select(group => group.First())
            .ToList();

        if (summaries.Count == 0)
        {
            return new SofaScoreApiAttemptResult(
                [],
                "browser-listing-api",
                $"SofaScore browser captured {responses.Count} API response(s) on {listingUrl}, but none contained parsable tennis event summaries.",
                responses.Count,
                responses.Count);
        }

        var resolvedScores = ResolveSofaScoreApiSummaryScores(fixtures, summaries);
        if (resolvedScores.Count > 0)
        {
            return new SofaScoreApiAttemptResult(
                resolvedScores,
                "browser-listing-api",
                $"SofaScore browser API capture matched {resolvedScores.Count} targeted fixture(s) from {summaries.Count} captured tennis event summary row(s).",
                summaries.Count,
                responses.Count);
        }

        return new SofaScoreApiAttemptResult(
            [],
            "browser-listing-api",
            $"SofaScore browser API capture parsed {summaries.Count} tennis event summary row(s) from {responses.Count} response(s) on {listingUrl}, but none matched the targeted fixtures.",
            summaries.Count,
            responses.Count);
    }

    private List<SofaScoreMatchScore> ResolveSofaScoreApiSummaryScores(
        IReadOnlyList<SofaScoreFixtureRequest> fixtures,
        IReadOnlyList<SofaScoreApiEventSummary> summaries)
    {
        if (fixtures.Count == 0 || summaries.Count == 0)
        {
            return [];
        }

        var remainingSummaries = summaries.ToList();
        var resolvedScores = new List<SofaScoreMatchScore>();

        foreach (var fixture in fixtures)
        {
            var bestSummary = remainingSummaries
                .Select(summary => new
                {
                    Summary = summary,
                    Score = ScoreSofaScoreApiSummaryCandidate(fixtures, fixture, summary)
                })
                .Where(entry => entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => Math.Abs(GetKickoffDeltaMinutes(entry.Summary.MatchTime, fixture.ScheduledMatchTimeUtc)))
                .Select(entry => entry.Summary)
                .FirstOrDefault();

            if (bestSummary is null)
            {
                continue;
            }

            resolvedScores.Add(bestSummary.ToMatchScore());
            remainingSummaries.Remove(bestSummary);
        }

        return resolvedScores;
    }

    private int ScoreSofaScoreApiSummaryCandidate(
        IReadOnlyList<SofaScoreFixtureRequest> allFixtures,
        SofaScoreFixtureRequest fixture,
        SofaScoreApiEventSummary candidate)
    {
        if (!TeamsLookEquivalent(candidate.HomeTeam, fixture.HomeTeam) ||
            !TeamsLookEquivalent(candidate.AwayTeam, fixture.AwayTeam))
        {
            return 0;
        }

        var score = 0;

        if (string.Equals(NormalizeFixtureKeyPart(candidate.HomeTeam), NormalizeFixtureKeyPart(fixture.HomeTeam), StringComparison.OrdinalIgnoreCase))
        {
            score += 45;
        }
        else
        {
            score += 25;
        }

        if (string.Equals(NormalizeFixtureKeyPart(candidate.AwayTeam), NormalizeFixtureKeyPart(fixture.AwayTeam), StringComparison.OrdinalIgnoreCase))
        {
            score += 45;
        }
        else
        {
            score += 25;
        }

        if (!string.IsNullOrWhiteSpace(candidate.League) &&
            !string.IsNullOrWhiteSpace(fixture.League) &&
            TeamsLookEquivalent(candidate.League, fixture.League))
        {
            score += 15;
        }

        var kickoffDeltaMinutes = Math.Abs(GetKickoffDeltaMinutes(candidate.MatchTime, fixture.ScheduledMatchTimeUtc));
        if (kickoffDeltaMinutes <= 5)
        {
            score += 25;
        }
        else if (kickoffDeltaMinutes <= 30)
        {
            score += 15;
        }
        else if (kickoffDeltaMinutes <= 90)
        {
            score += 5;
        }

        if (candidate.IsLive)
        {
            score += 5;
        }

        var sameDayFixtureCollisions = allFixtures.Count(other =>
            other.MatchLocalDate == fixture.MatchLocalDate &&
            TeamsLookEquivalent(other.HomeTeam, fixture.HomeTeam) &&
            TeamsLookEquivalent(other.AwayTeam, fixture.AwayTeam));

        if (sameDayFixtureCollisions > 1)
        {
            score -= 15;
        }

        return score;
    }

    private IReadOnlyList<SofaScoreListingEntry> ExtractSofaScoreBrowserDomEntries(
        ChromeDriver driver,
        string baseUrl)
    {
        try
        {
            var js = (IJavaScriptExecutor)driver;
            var rawJson = js.ExecuteScript(
                """
                const normalize = value => (value || '').replace(/\u00a0/g, ' ').trim();
                const rows = Array.from(document.querySelectorAll("a[href*='/tennis/match/']")).map(anchor => {
                  const row = anchor.closest('a[data-id]') || anchor;
                  const section = row.closest('.pb_sm') || row.parentElement;
                  const titleNode = row.querySelector('[title*="live score"], [title*="score"]');
                  return {
                    href: anchor.href || anchor.getAttribute('href') || '',
                    text: normalize(row.innerText || row.textContent || ''),
                    sectionText: normalize(section ? (section.innerText || section.textContent || '') : ''),
                    title: normalize((titleNode && titleNode.getAttribute('title')) || row.getAttribute('title') || '')
                  };
                });
                return JSON.stringify(rows);
                """)?.ToString();

            var candidates = SofaScoreBrowserDomParser.ParseCandidates(rawJson);
            return SofaScoreBrowserDomParser.ParseEntries(candidates, baseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SofaScore browser DOM text extraction failed.");
            return [];
        }
    }

    private void EnsureSofaScoreBrowserCaptureInstalled(ChromeDriver driver)
    {
        try
        {
            driver.ExecuteCdpCommand(
                "Page.addScriptToEvaluateOnNewDocument",
                new Dictionary<string, object>
                {
                    ["source"] =
                        """
                        (() => {
                          if (window.__tennisPredictorSofaCaptureInstalled) {
                            return;
                          }

                          const shouldCapture = rawUrl => {
                            try {
                              const url = new URL(rawUrl, location.origin);
                              return url.pathname.includes('/api/v1/event/') ||
                                     (url.pathname.includes('/api/v1/sport/tennis/') &&
                                      (url.pathname.includes('/events/live') || url.pathname.includes('/scheduled-events/')));
                            } catch {
                              return false;
                            }
                          };

                          const normalizePath = rawUrl => {
                            try {
                              const url = new URL(rawUrl, location.origin);
                              return `${url.pathname}${url.search}`;
                            } catch {
                              return rawUrl || '';
                            }
                          };

                          const pushResponse = payload => {
                            window.__tennisPredictorSofaResponses = window.__tennisPredictorSofaResponses || [];
                            window.__tennisPredictorSofaResponses.push(payload);
                            if (window.__tennisPredictorSofaResponses.length > 250) {
                              window.__tennisPredictorSofaResponses.splice(0, window.__tennisPredictorSofaResponses.length - 250);
                            }
                          };

                          window.__tennisPredictorSofaResponses = window.__tennisPredictorSofaResponses || [];
                          window.__tennisPredictorSofaCaptureInstalled = true;

                          const originalFetch = window.fetch.bind(window);
                          window.fetch = async (...args) => {
                            const response = await originalFetch(...args);
                            try {
                              const url = typeof args[0] === 'string' ? args[0] : args[0]?.url;
                              if (shouldCapture(url)) {
                                const cloned = response.clone();
                                cloned.text().then(body => pushResponse({
                                  relativePath: normalizePath(url),
                                  ok: response.ok,
                                  status: response.status,
                                  body: body
                                })).catch(() => {});
                              }
                            } catch {}
                            return response;
                          };

                          const originalOpen = XMLHttpRequest.prototype.open;
                          const originalSend = XMLHttpRequest.prototype.send;

                          XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                            this.__tennisPredictorSofaUrl = url;
                            return originalOpen.call(this, method, url, ...rest);
                          };

                          XMLHttpRequest.prototype.send = function(body) {
                            this.addEventListener('loadend', function() {
                              try {
                                const url = this.__tennisPredictorSofaUrl;
                                if (shouldCapture(url)) {
                                  pushResponse({
                                    relativePath: normalizePath(url),
                                    ok: this.status >= 200 && this.status < 300,
                                    status: this.status,
                                    body: typeof this.responseText === 'string' ? this.responseText : ''
                                  });
                                }
                              } catch {}
                            });

                            return originalSend.call(this, body);
                          };
                        })();
                        """
                });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SofaScore browser capture installation failed.");
        }
    }

    private static void ClearSofaScoreBrowserCapturedResponses(ChromeDriver driver)
    {
        try
        {
            var js = (IJavaScriptExecutor)driver;
            js.ExecuteScript("window.__tennisPredictorSofaResponses = [];");
        }
        catch
        {
            // Ignore best-effort cleanup failures.
        }
    }

    private IReadOnlyList<SofaScoreBrowserFetchResponse> ReadSofaScoreBrowserCapturedResponses(ChromeDriver driver)
    {
        try
        {
            var js = (IJavaScriptExecutor)driver;
            var rawJson = js.ExecuteScript("return JSON.stringify(window.__tennisPredictorSofaResponses || []);")?.ToString();
            return SofaScoreBrowserFetchParser.ParseResponses(rawJson)
                .GroupBy(response => $"{response.RelativePath}|{response.Status}|{response.Ok}|{response.Body}", StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading captured SofaScore browser responses failed.");
            return [];
        }
    }

    private async Task<SofaScoreApiAttemptResult> FetchSofaScoreBrowserEventPagesAsync(
        IReadOnlyList<SofaScoreFixtureRequest> fixtures,
        IReadOnlyDictionary<string, List<string>> eventUrlsByFixture,
        ChromeDriver? sharedDriver = null)
    {
        if (sharedDriver is null)
        {
            return await RunWithChromeSessionAsync(
                driver => FetchSofaScoreBrowserEventPagesCoreAsync(fixtures, eventUrlsByFixture, driver),
                ConfigureSofaScoreBrowserOptions,
                "SofaScore browser event fetch");
        }

        return await FetchSofaScoreBrowserEventPagesCoreAsync(fixtures, eventUrlsByFixture, sharedDriver);
    }

    private async Task<SofaScoreApiAttemptResult> FetchSofaScoreBrowserEventPagesCoreAsync(
        IReadOnlyList<SofaScoreFixtureRequest> fixtures,
        IReadOnlyDictionary<string, List<string>> eventUrlsByFixture,
        ChromeDriver driver)
    {
        var baseUrl = (_configuration["ScrapingValues:SofaScoreBaseUrl"] ?? "https://www.sofascore.com").TrimEnd('/');
        var eventPageBudget = ParseConfiguredInt("ScrapingValues:SofaScoreMaxEventPagesPerRun", DefaultSofaScoreMaxEventPagesPerRun);
        var pagesFetched = 0;
        var scrapedScores = new List<SofaScoreMatchScore>();
        var emptyHtmlCount = 0;
        var parseFailureCount = 0;
        var fixtureMismatchCount = 0;
        var apiDetailCount = 0;
        var apiDetailMismatchCount = 0;

        foreach (var fixture in fixtures)
        {
            if (!eventUrlsByFixture.TryGetValue(BuildSofaScoreFixtureCacheKey(fixture), out var candidateUrls))
            {
                continue;
            }

            foreach (var candidateUrl in candidateUrls)
            {
                if (pagesFetched >= eventPageBudget)
                {
                    return new SofaScoreApiAttemptResult(
                        scrapedScores,
                        "browser-event-page",
                        $"SofaScore browser event-page budget of {eventPageBudget} was reached after {pagesFetched} detail page(s).",
                        eventUrlsByFixture.Values.Sum(urls => urls.Count),
                        pagesFetched);
                }

                pagesFetched++;
                try
                {
                    ClearSofaScoreBrowserCapturedResponses(driver);
                    await driver.Navigate().GoToUrlAsync(candidateUrl);
                    WaitForDocumentReady(driver);
                    DismissCookieBanners(driver);
                    await Task.Delay(1500);

                    var capturedResponses = ReadSofaScoreBrowserCapturedResponses(driver);
                    var apiDetailScores = SofaScoreBrowserFetchParser.ParseEventDetails(capturedResponses, baseUrl);
                    if (apiDetailScores.Count > 0)
                    {
                        apiDetailCount += apiDetailScores.Count;

                        var apiMatchedScore = apiDetailScores.Values
                            .Where(score => SofaScoreEventMatchesFixture(score, fixture))
                            .OrderByDescending(score => ScoreDirectSofaScoreCandidate(fixture, score))
                            .FirstOrDefault();

                        if (apiMatchedScore is not null)
                        {
                            scrapedScores.Add(apiMatchedScore);
                            break;
                        }

                        apiDetailMismatchCount++;
                    }

                    var html = driver.PageSource;
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        emptyHtmlCount++;
                        continue;
                    }

                    if (!SofaScoreEventPageParser.TryParse(html, candidateUrl, out var parsedScore))
                    {
                        parseFailureCount++;
                        continue;
                    }

                    if (!SofaScoreEventMatchesFixture(parsedScore, fixture))
                    {
                        fixtureMismatchCount++;
                        continue;
                    }

                    scrapedScores.Add(parsedScore);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SofaScore browser event page fetch failed for {Url}.", candidateUrl);
                }
            }
        }

        return new SofaScoreApiAttemptResult(
            scrapedScores,
            "browser-event-page",
            scrapedScores.Count > 0
                ? $"SofaScore browser crawler parsed {scrapedScores.Count} targeted event page(s) from {eventUrlsByFixture.Values.Sum(urls => urls.Count)} candidate URL(s)."
                : $"SofaScore browser crawler found {eventUrlsByFixture.Values.Sum(urls => urls.Count)} candidate URL(s) but matched none. Captured API detail payloads: {apiDetailCount}; API detail mismatches: {apiDetailMismatchCount}; empty HTML pages: {emptyHtmlCount}; parse failures: {parseFailureCount}; fixture mismatches: {fixtureMismatchCount}.",
            eventUrlsByFixture.Values.Sum(urls => urls.Count),
            pagesFetched);
    }

    private async Task<HashSet<string>> CollectSofaScoreCandidateUrlsViaBrowserAsync(
        ChromeDriver driver,
        IReadOnlyList<string> listingUrls)
    {
        var discoveredUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scrollRounds = ParseConfiguredInt("ScrapingValues:SofaScoreBrowserScrollRounds", DefaultSofaScoreBrowserScrollRounds);
        var listingPageBudget = ParseConfiguredInt("ScrapingValues:SofaScoreBrowserListingPagesPerRun", DefaultSofaScoreBrowserListingPagesPerRun);
        var pagesVisited = 0;
        var baseUrl = (_configuration["ScrapingValues:SofaScoreBaseUrl"] ?? "https://www.sofascore.com").TrimEnd('/');

        foreach (var listingUrl in listingUrls.Take(listingPageBudget))
        {
            pagesVisited++;
            try
            {
                await driver.Navigate().GoToUrlAsync(listingUrl);
                WaitForDocumentReady(driver);
                DismissCookieBanners(driver);
                await Task.Delay(2500);

                for (var round = 0; round < scrollRounds; round++)
                {
                    MergeSofaScoreBrowserUrls(discoveredUrls, driver.PageSource, baseUrl);
                    TryClickSofaScoreExpanders(driver);

                    var js = (IJavaScriptExecutor)driver;
                    js.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                    await Task.Delay(1200);
                }

                MergeSofaScoreBrowserUrls(discoveredUrls, driver.PageSource, baseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SofaScore browser listing fetch failed for {Url}.", listingUrl);
            }
        }

        _logger.LogInformation(
            "SofaScore browser discovery harvested {UrlCount} candidate match URL(s) from {PageCount} rendered listing page(s).",
            discoveredUrls.Count,
            pagesVisited);

        return discoveredUrls;
    }

    private IReadOnlyList<string> BuildSofaScoreBrowserListingUrls()
    {
        var configured = _configuration["ScrapingValues:SofaScoreBrowserListingUrls"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var baseUrl = (_configuration["ScrapingValues:SofaScoreBaseUrl"] ?? "https://www.sofascore.com").TrimEnd('/');
        return
        [
            $"{baseUrl}/tennis",
            baseUrl
        ];
    }

    private void MergeSofaScoreBrowserUrls(HashSet<string> discoveredUrls, string html, string baseUrl)
    {
        foreach (var entry in SofaScoreListingPageParser.ParseEntries(html, baseUrl))
        {
            if (!string.IsNullOrWhiteSpace(entry.EventUrl))
            {
                discoveredUrls.Add(entry.EventUrl);
            }
        }

        foreach (var url in SofaScoreDiscoveryHelper.ExtractMatchUrlsFromHtml(html, baseUrl))
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                discoveredUrls.Add(url);
            }
        }
    }

    private static void TryClickSofaScoreExpanders(IWebDriver driver)
    {
        var js = (IJavaScriptExecutor)driver;
        js.ExecuteScript(@"
            const labels = ['show more', 'load more', 'more matches', 'all matches'];
            const nodes = Array.from(document.querySelectorAll('button, [role=""button""], a'));
            for (const node of nodes) {
              const text = (node.textContent || '').trim().toLowerCase();
              if (!text) continue;
              if (labels.some(label => text.includes(label))) {
                try { node.click(); } catch (_) {}
              }
            }
        ");
    }

    private async Task<List<SofaScoreMatchScore>> ScrapeSofaScoreViaHtmlFallbackAsync(
        IReadOnlyList<SofaScoreFixtureRequest> requestedFixtures,
        HttpClient client)
    {
        var eventUrlsByFixture = await DiscoverSofaScoreEventUrlsAsync(requestedFixtures, client);
        if (eventUrlsByFixture.Count == 0)
        {
            _logger.LogInformation("SofaScore HTML fallback discovery returned no event URLs for {FixtureCount} targeted fixtures.", requestedFixtures.Count);
            return [];
        }

        var eventPageBudget = ParseConfiguredInt("ScrapingValues:SofaScoreMaxEventPagesPerRun", DefaultSofaScoreMaxEventPagesPerRun);
        var scrapedScores = new List<SofaScoreMatchScore>();
        var pagesFetched = 0;
        var candidateUrlCount = eventUrlsByFixture.Values.Sum(urls => urls.Count);
        var emptyHtmlCount = 0;
        var parseFailureCount = 0;
        var fixtureMismatchCount = 0;

        foreach (var fixture in requestedFixtures)
        {
            if (!eventUrlsByFixture.TryGetValue(BuildSofaScoreFixtureCacheKey(fixture), out var candidateUrls))
            {
                continue;
            }

            foreach (var candidateUrl in candidateUrls)
            {
                if (pagesFetched >= eventPageBudget)
                {
                    _logger.LogWarning(
                        "SofaScore event-page budget of {Budget} reached. Stopping targeted fetches for this run.",
                        eventPageBudget);
                    return scrapedScores;
                }

                pagesFetched++;
                var html = await TryFetchSofaScorePageAsync(client, candidateUrl);
                if (string.IsNullOrWhiteSpace(html))
                {
                    emptyHtmlCount++;
                    continue;
                }

                if (!SofaScoreEventPageParser.TryParse(html, candidateUrl, out var parsedScore))
                {
                    parseFailureCount++;
                    continue;
                }

                if (!SofaScoreEventMatchesFixture(parsedScore, fixture))
                {
                    fixtureMismatchCount++;
                    continue;
                }

                scrapedScores.Add(parsedScore);
                break;
            }
        }

        _logger.LogInformation(
            "Scraped {Count} SofaScore HTML fallback pages for {FixtureCount} incomplete fixtures.",
            scrapedScores.Count,
            requestedFixtures.Count);

        if (scrapedScores.Count > 0)
        {
            _sofaScoreSourceHealthTracker.RecordSuccess(
                "event-page-fallback",
                scrapedScores.Count,
                candidateUrlCount,
                pagesFetched,
                $"SofaScore HTML fallback scraped {scrapedScores.Count} targeted match page(s) from {candidateUrlCount} candidate URL(s).");
        }
        else
        {
            _sofaScoreSourceHealthTracker.RecordEmpty(
                "event-page-fallback",
                $"SofaScore HTML fallback found {candidateUrlCount} candidate URL(s) but matched none. Empty HTML pages: {emptyHtmlCount}; parse failures: {parseFailureCount}; fixture mismatches: {fixtureMismatchCount}.",
                candidateUrlCount,
                pagesFetched);
        }

        return scrapedScores;
    }

    private List<SofaScoreMatchScore> ResolveSofaScoreListingScores(
        IReadOnlyList<SofaScoreFixtureRequest> fixtures,
        IReadOnlyList<SofaScoreListingEntry> candidates)
    {
        if (fixtures.Count == 0 || candidates.Count == 0)
        {
            return [];
        }

        var remainingCandidates = candidates.ToList();
        var resolvedScores = new List<SofaScoreMatchScore>();

        foreach (var fixture in fixtures)
        {
            var bestCandidate = remainingCandidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = ScoreSofaScoreListingCandidate(fixtures, fixture, candidate)
                })
                .Where(entry => entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => Math.Abs(GetKickoffDeltaMinutes(
                    ResolveSofaScoreListingMatchTimeUtc(fixture, entry.Candidate),
                    fixture.ScheduledMatchTimeUtc)))
                .Select(entry => entry.Candidate)
                .FirstOrDefault();

            if (bestCandidate is null)
            {
                continue;
            }

            var scoreSummary = BuildSetScoreSummary(bestCandidate.Score);

            resolvedScores.Add(new SofaScoreMatchScore
            {
                League = string.IsNullOrWhiteSpace(bestCandidate.League) ? fixture.League : bestCandidate.League,
                HomeTeam = bestCandidate.HomeTeam,
                AwayTeam = bestCandidate.AwayTeam,
                Score = bestCandidate.Score,
                NormalizedScoreline = scoreSummary.NormalizedScoreline,
                HomeSetsWon = scoreSummary.HomeSetsWon,
                AwaySetsWon = scoreSummary.AwaySetsWon,
                DisplayedScore = bestCandidate.Score,
                RegularTimeScore = bestCandidate.IsLive ? null : bestCandidate.Score,
                StatusText = bestCandidate.StatusText,
                EventUrl = bestCandidate.EventUrl,
                MatchTime = ResolveSofaScoreListingMatchTimeUtc(fixture, bestCandidate),
                IsLive = bestCandidate.IsLive
            });

            remainingCandidates.Remove(bestCandidate);
        }

        return resolvedScores;
    }

    private int ScoreSofaScoreListingCandidate(
        IReadOnlyList<SofaScoreFixtureRequest> allFixtures,
        SofaScoreFixtureRequest fixture,
        SofaScoreListingEntry candidate)
    {
        if (!TeamsLookEquivalent(candidate.HomeTeam, fixture.HomeTeam) ||
            !TeamsLookEquivalent(candidate.AwayTeam, fixture.AwayTeam))
        {
            return 0;
        }

        var score = 0;

        if (string.Equals(NormalizeFixtureKeyPart(candidate.HomeTeam), NormalizeFixtureKeyPart(fixture.HomeTeam), StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }
        else
        {
            score += 20;
        }

        if (string.Equals(NormalizeFixtureKeyPart(candidate.AwayTeam), NormalizeFixtureKeyPart(fixture.AwayTeam), StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }
        else
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(candidate.League) &&
            !string.IsNullOrWhiteSpace(fixture.League) &&
            TeamsLookEquivalent(candidate.League, fixture.League))
        {
            score += 15;
        }

        var urlScore = SofaScoreDiscoveryHelper.ScoreUrlAgainstFixture(
            candidate.EventUrl,
            SofaScoreDiscoveryHelper.BuildSlugCandidates(fixture.HomeTeam),
            SofaScoreDiscoveryHelper.BuildSlugCandidates(fixture.AwayTeam));
        score += Math.Min(30, urlScore);

        var candidateMatchTimeUtc = ResolveSofaScoreListingMatchTimeUtc(fixture, candidate);
        var kickoffDeltaMinutes = Math.Abs(GetKickoffDeltaMinutes(candidateMatchTimeUtc, fixture.ScheduledMatchTimeUtc));
        if (kickoffDeltaMinutes <= 5)
        {
            score += 25;
        }
        else if (kickoffDeltaMinutes <= 30)
        {
            score += 15;
        }
        else if (kickoffDeltaMinutes <= 90)
        {
            score += 5;
        }

        if (candidate.IsLive)
        {
            score += 5;
        }

        var sameDayFixtureCollisions = allFixtures.Count(other =>
            other.MatchLocalDate == fixture.MatchLocalDate &&
            TeamsLookEquivalent(other.HomeTeam, fixture.HomeTeam) &&
            TeamsLookEquivalent(other.AwayTeam, fixture.AwayTeam));

        if (sameDayFixtureCollisions > 1)
        {
            score -= 15;
        }

        return score;
    }

    private static DateTime ResolveSofaScoreListingMatchTimeUtc(
        SofaScoreFixtureRequest fixture,
        SofaScoreListingEntry candidate)
    {
        if (candidate.KickoffLocalTime.HasValue && fixture.MatchLocalDate != default)
        {
            var localKickoff = fixture.MatchLocalDate.ToDateTime(candidate.KickoffLocalTime.Value);
            return DateTimeProvider.ConvertLocalToUtc(localKickoff);
        }

        return fixture.ScheduledMatchTimeUtc ?? DateTime.UtcNow;
    }

    private static double GetKickoffDeltaMinutes(DateTime candidateMatchTimeUtc, DateTime? scheduledMatchTimeUtc)
    {
        if (!scheduledMatchTimeUtc.HasValue)
        {
            return double.MaxValue;
        }

        return Math.Abs((candidateMatchTimeUtc - scheduledMatchTimeUtc.Value).TotalMinutes);
    }

    private async Task<Dictionary<string, List<string>>> DiscoverSofaScoreEventUrlsAsync(
        IReadOnlyList<SofaScoreFixtureRequest> fixtures,
        HttpClient client)
    {
        var nowUtc = DateTime.UtcNow;
        var eventUrlsByFixture = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unresolvedFixtures = new List<SofaScoreFixtureRequest>();

        foreach (var fixture in fixtures)
        {
            var cacheKey = BuildSofaScoreFixtureCacheKey(fixture);
            if (SofaScoreEventUrlCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresAtUtc > nowUtc &&
                !string.IsNullOrWhiteSpace(cached.EventUrl))
            {
                eventUrlsByFixture[cacheKey] = [cached.EventUrl];
                continue;
            }

            unresolvedFixtures.Add(fixture);
        }

        if (unresolvedFixtures.Count == 0)
        {
            return eventUrlsByFixture;
        }

        var baseUrl = (_configuration["ScrapingValues:SofaScoreBaseUrl"] ?? "https://www.sofascore.com").TrimEnd('/');
        var robotsText = await TryFetchSofaScoreTextAsync(client, $"{baseUrl}/robots.txt", "html-discovery");
        if (string.IsNullOrWhiteSpace(robotsText))
        {
            return eventUrlsByFixture;
        }

        var sitemapRoots = SofaScoreDiscoveryHelper.ParseRobotSitemapUrls(robotsText)
            .Where(url => url.Contains("sitemap", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(url => url.Contains("event", StringComparison.OrdinalIgnoreCase))
            .ThenBy(url => url)
            .ToList();

        if (sitemapRoots.Count == 0)
        {
            _sofaScoreSourceHealthTracker.RecordEmpty("discovery", "SofaScore robots.txt did not expose any sitemap URLs.");
            return eventUrlsByFixture;
        }

        var maxSitemaps = ParseConfiguredInt("ScrapingValues:SofaScoreMaxSitemapsPerRun", DefaultSofaScoreMaxSitemapsPerRun);
        var maxCandidateUrlsPerFixture = ParseConfiguredInt("ScrapingValues:SofaScoreMaxCandidateUrlsPerFixture", DefaultSofaScoreMaxCandidateUrlsPerFixture);
        var candidateUrlPool = await LoadSofaScoreEventUrlsAsync(client, sitemapRoots, maxSitemaps);
        var candidateUrls = candidateUrlPool.Urls;
        if (candidateUrls.Count == 0)
        {
            _sofaScoreSourceHealthTracker.RecordEmpty(
                "discovery",
                $"SofaScore processed {candidateUrlPool.SitemapsProcessed} sitemap(s) but discovered no tennis event URLs.");
            return eventUrlsByFixture;
        }

        foreach (var fixture in unresolvedFixtures)
        {
            var cacheKey = BuildSofaScoreFixtureCacheKey(fixture);
            var homeSlugs = SofaScoreDiscoveryHelper.BuildSlugCandidates(fixture.HomeTeam);
            var awaySlugs = SofaScoreDiscoveryHelper.BuildSlugCandidates(fixture.AwayTeam);

            var matches = candidateUrls
                .Select(url => new
                {
                    Url = url,
                    Score = SofaScoreDiscoveryHelper.ScoreUrlAgainstFixture(url, homeSlugs, awaySlugs)
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Url)
                .Take(maxCandidateUrlsPerFixture)
                .Select(candidate => candidate.Url)
                .ToList();

            if (matches.Count == 0)
            {
                continue;
            }

            eventUrlsByFixture[cacheKey] = matches;
            SofaScoreEventUrlCache[cacheKey] = new SofaScoreDiscoveryCacheEntry(matches[0], nowUtc.Add(SofaScoreDiscoveryCacheLifetime));
        }

        if (eventUrlsByFixture.Count == 0)
        {
            _sofaScoreSourceHealthTracker.RecordEmpty(
                "discovery",
                $"SofaScore discovered {candidateUrls.Count} tennis event URL(s) across {candidateUrlPool.SitemapsProcessed} sitemap(s) but none matched the {unresolvedFixtures.Count} targeted fixture slug pairs.");
        }

        return eventUrlsByFixture;
    }

    private static void PruneExpiredSofaScoreEventUrlCache(DateTime nowUtc)
    {
        foreach (var entry in SofaScoreEventUrlCache)
        {
            if (entry.Value.ExpiresAtUtc <= nowUtc)
            {
                SofaScoreEventUrlCache.TryRemove(entry.Key, out _);
            }
        }
    }

    private async Task<SofaScoreEventUrlPoolResult> LoadSofaScoreEventUrlsAsync(
        HttpClient client,
        IReadOnlyList<string> sitemapRoots,
        int maxSitemaps)
    {
        var nowUtc = DateTime.UtcNow;
        lock (SofaScoreSitemapPoolGate)
        {
            if (_sofaScoreSitemapPoolCache is not null &&
                _sofaScoreSitemapPoolCache.ExpiresAtUtc > nowUtc &&
                _sofaScoreSitemapPoolCache.Urls.Count > 0)
            {
                return new SofaScoreEventUrlPoolResult(
                    _sofaScoreSitemapPoolCache.Urls.ToList(),
                    0,
                    true);
            }
        }

        var queue = new Queue<string>(sitemapRoots);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eventUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;

        while (queue.Count > 0 && processed < maxSitemaps)
        {
            var sitemapUrl = queue.Dequeue();
            if (!visited.Add(sitemapUrl))
            {
                continue;
            }

            processed++;
            var payload = await TryFetchSofaScoreBytesAsync(client, sitemapUrl, "html-sitemap");
            if (payload.Length == 0)
            {
                continue;
            }

            IReadOnlyList<SofaScoreSitemapEntry> entries;
            try
            {
                entries = SofaScoreDiscoveryHelper.ParseSitemapEntries(payload, sitemapUrl);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable SofaScore sitemap {SitemapUrl}.", sitemapUrl);
                continue;
            }

            foreach (var entry in entries
                         .OrderByDescending(entry => ScoreSofaScoreLocationPriority(entry.Location))
                         .ThenByDescending(entry => entry.LastModifiedUtc ?? DateTime.MinValue))
            {
                if (entry.Location.Contains("/tennis/match/", StringComparison.OrdinalIgnoreCase))
                {
                    eventUrls.Add(entry.Location);
                    continue;
                }

                if ((entry.Location.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                     entry.Location.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase) ||
                     entry.Location.Contains("sitemap", StringComparison.OrdinalIgnoreCase)) &&
                    !visited.Contains(entry.Location))
                {
                    queue.Enqueue(entry.Location);
                }
            }
        }

        var result = new SofaScoreEventUrlPoolResult(eventUrls.ToList(), processed, false);
        if (result.Urls.Count > 0)
        {
            lock (SofaScoreSitemapPoolGate)
            {
                _sofaScoreSitemapPoolCache = new SofaScoreSitemapPoolCacheEntry(
                    result.Urls,
                    nowUtc.Add(SofaScoreSitemapPoolCacheLifetime));
            }
        }

        return result;
    }

    private async Task<string?> TryFetchSofaScorePageAsync(HttpClient client, string url)
    {
        var bytes = await TryFetchSofaScoreBytesAsync(client, url, "event-page-fallback");
        return bytes.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<string?> TryFetchSofaScoreTextAsync(HttpClient client, string url, string stage)
    {
        var bytes = await TryFetchSofaScoreBytesAsync(client, url, stage);
        return bytes.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> TryFetchSofaScoreBytesAsync(HttpClient client, string url, string stage)
    {
        try
        {
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 403 or 429)
                {
                    _sofaScoreSourceHealthTracker.RecordBlocked(stage, $"SofaScore returned {(int)response.StatusCode} for {url}.");
                }
                _logger.LogDebug("SofaScore fetch for {Url} returned {StatusCode}.", url, response.StatusCode);
                return [];
            }

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SofaScore fetch failed for {Url}.", url);
            return [];
        }
    }

    private bool SofaScoreEventMatchesFixture(SofaScoreMatchScore parsedScore, SofaScoreFixtureRequest fixture)
    {
        if (!TeamsLookEquivalent(parsedScore.HomeTeam, fixture.HomeTeam) ||
            !TeamsLookEquivalent(parsedScore.AwayTeam, fixture.AwayTeam))
        {
            return false;
        }

        var score = 0;

        score += string.Equals(
            NormalizeFixtureKeyPart(parsedScore.HomeTeam),
            NormalizeFixtureKeyPart(fixture.HomeTeam),
            StringComparison.OrdinalIgnoreCase)
            ? 45
            : 25;

        score += string.Equals(
            NormalizeFixtureKeyPart(parsedScore.AwayTeam),
            NormalizeFixtureKeyPart(fixture.AwayTeam),
            StringComparison.OrdinalIgnoreCase)
            ? 45
            : 25;

        var parsedLocalDate = DateTimeProvider.ConvertUtcToLocal(parsedScore.MatchTime).Date;
        var fixtureLocalDate = fixture.MatchLocalDate.ToDateTime(TimeOnly.MinValue).Date;
        var kickoffDeltaMinutes = Math.Abs(GetKickoffDeltaMinutes(parsedScore.MatchTime, fixture.ScheduledMatchTimeUtc));

        if (parsedLocalDate == fixtureLocalDate)
        {
            score += 15;
        }
        else if (kickoffDeltaMinutes <= 12 * 60)
        {
            // Allow for SofaScore detail pages that drift the visible date across UTC/local boundaries.
            score += 5;
        }
        else
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(parsedScore.League) &&
            !string.IsNullOrWhiteSpace(fixture.League) &&
            TeamsLookEquivalent(parsedScore.League, fixture.League))
        {
            score += 10;
        }

        if (kickoffDeltaMinutes <= 15)
        {
            score += 20;
        }
        else if (kickoffDeltaMinutes <= 90)
        {
            score += 10;
        }
        else if (kickoffDeltaMinutes <= 12 * 60)
        {
            score += 4;
        }

        if (!parsedScore.IsLive)
        {
            score += 5;
        }

        return score >= 70;
    }

    private static bool TeamsLookEquivalent(string left, string right)
    {
        var leftCandidates = SofaScoreDiscoveryHelper.BuildSlugCandidates(left);
        var rightCandidates = SofaScoreDiscoveryHelper.BuildSlugCandidates(right);

        if (leftCandidates.Count == 0 || rightCandidates.Count == 0)
        {
            return false;
        }

        if (leftCandidates.Intersect(rightCandidates, StringComparer.OrdinalIgnoreCase).Any())
        {
            return true;
        }

        var leftLongest = leftCandidates[0];
        var rightLongest = rightCandidates[0];
        return leftLongest.Contains(rightLongest, StringComparison.OrdinalIgnoreCase) ||
               rightLongest.Contains(leftLongest, StringComparison.OrdinalIgnoreCase);
    }

    private int ParseConfiguredInt(string key, int fallback)
    {
        return int.TryParse(_configuration[key], out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private int ParseConfiguredSignedInt(string key, int fallback)
    {
        return int.TryParse(_configuration[key], out var parsed)
            ? parsed
            : fallback;
    }

    private static bool ResolveBrowserScrapingEnabled(IConfiguration configuration)
    {
        var rawValue = configuration["ENABLE_BROWSER_SCRAPING"];
        if (TryParseBoolean(rawValue, out var explicitValue))
        {
            return explicitValue;
        }

        rawValue = configuration["RUN_BACKGROUND_JOBS"];
        return TryParseBoolean(rawValue, out var backgroundJobsEnabled)
            ? backgroundJobsEnabled
            : true;
    }

    private static bool TryParseBoolean(string? rawValue, out bool parsedValue)
    {
        if (bool.TryParse(rawValue, out parsedValue))
        {
            return true;
        }

        switch (rawValue?.Trim().ToLowerInvariant())
        {
            case "1":
            case "yes":
            case "on":
                parsedValue = true;
                return true;
            case "0":
            case "no":
            case "off":
                parsedValue = false;
                return true;
            default:
                parsedValue = false;
                return false;
        }
    }

    private void EnsureBrowserScrapingEnabled(string operationName)
    {
        if (_browserScrapingEnabled)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Browser scraping is disabled for this service, so {operationName} cannot run here. Set ENABLE_BROWSER_SCRAPING=true on the worker service.");
    }

    private static int ScoreSofaScoreLocationPriority(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return 0;
        }

        var score = 0;
        if (location.Contains("/tennis/match/", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (location.Contains("tennis", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        if (location.Contains("event", StringComparison.OrdinalIgnoreCase))
        {
            score += 15;
        }

        if (location.Contains("live", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        return score;
    }

    private static bool LooksLikeAiScoreChallengePage(string html, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(html) && string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(title) &&
                title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(html) &&
                (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                 html.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase) ||
                 html.Contains("security verification", StringComparison.OrdinalIgnoreCase) ||
                 html.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool LooksLikeSofaScoreChallengePage(string html, string? title = null)
    {
        return LooksLikeAiScoreChallengePage(html, title) ||
               (!string.IsNullOrWhiteSpace(title) &&
                title.Contains("Access denied", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(html) &&
                html.Contains("Access denied", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalizes various score formats ("1 - 0", "1-0", "1 : 0", "1:0") to "H:A" format.
    /// </summary>
    private static string? NormalizeScore(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        
        // Remove extra whitespace
        var cleaned = raw.Trim();
        
        // Try to extract two numbers from the score string
        var scoreMatch = Regex.Match(cleaned, @"(\d+)\s*[-:–—]\s*(\d+)");
        if (scoreMatch.Success)
        {
            return $"{scoreMatch.Groups[1].Value}:{scoreMatch.Groups[2].Value}";
        }
        
        return null;
    }

    /// <summary>
    /// Internal DTO for deserializing JS extraction results
    /// </summary>
    private sealed record AiScoreAttemptResult(
        List<AiScoreMatchScore> Matches,
        AiScoreAttemptStatus Status,
        string Detail);

    private enum AiScoreAttemptStatus
    {
        Success,
        Empty,
        Blocked,
        Failed
    }

    private sealed record SofaScoreDiscoveryCacheEntry(string EventUrl, DateTime ExpiresAtUtc);
    private sealed record SofaScoreSitemapPoolCacheEntry(IReadOnlyList<string> Urls, DateTime ExpiresAtUtc);
    private sealed record SofaScoreEventUrlPoolResult(List<string> Urls, int SitemapsProcessed, bool FromCache);
    private sealed record SofaScoreApiAttemptResult(
        List<SofaScoreMatchScore> Scores,
        string Stage,
        string Detail,
        int CandidateCount,
        int DetailFetchCount);

    private sealed class AiScoreRawMatch
    {
        public string? Home { get; set; }
        public string? Away { get; set; }
        public string? Score { get; set; }
        public string? Time { get; set; }
        public string? League { get; set; }
        public bool IsLive { get; set; }
    }

    private async Task CheckFileIsDownloaded(DateTime scrapeStartedAtUtc)
    {
        var fileName = _configuration["ScrapingValues:PredictionsFileName"]
                       ?? throw new InvalidOperationException("Predictions file name not configured in appsettings.json");
        string[] possiblePaths =
        [
            Path.Combine(_downloadFolder, fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", fileName)
        ];

        _ = int.TryParse(_configuration["ScrapingValues:ScrapingMaxWaitTime"], out var maxWaitTime);
        _ = int.TryParse(_configuration["ScrapingValues:ScrapingMaxWaitInterval"], out var waitInterval);
        var totalWaitTime = 0;

        while (totalWaitTime < maxWaitTime * 1000)
        {
            foreach (var path in possiblePaths)
            {
                _logger.LogInformation("Checking for file at: {Path}", path);
                if (!File.Exists(path)) continue;

                var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                if (lastWriteTimeUtc < scrapeStartedAtUtc.AddSeconds(-2))
                {
                    _logger.LogInformation(
                        "Ignoring stale file at {Path}. Last write time {LastWriteTimeUtc:o} was before scrape start {ScrapeStartedAtUtc:o}.",
                        path,
                        lastWriteTimeUtc,
                        scrapeStartedAtUtc);
                    continue;
                }

                _logger.LogInformation("File found at: {Path}", path);
                if (path == Path.Combine(_downloadFolder, fileName)) return;
                File.Move(path, Path.Combine(_downloadFolder, fileName), true);
                _logger.LogInformation("File moved to download folder: {DownloadFolder}", _downloadFolder);
                return;
            }
            await Task.Delay(waitInterval);
            totalWaitTime += waitInterval;
        }

        throw new FileNotFoundException($"File {fileName} not found in any expected location after {maxWaitTime} seconds.");
    }
    
    private void DeletePreviousFile()
    {
        if (!Directory.Exists(_downloadFolder)) return;
        
        foreach (var filePath in Directory.GetFiles(_downloadFolder))
        {
            var fileName = Path.GetFileName(filePath);
            
            // Skip system files
            if (fileName == ".DS_Store" || fileName == ".gitkeep") continue;
            
            try
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted file: {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete file: {FilePath}", filePath);
            }
        }
    }

    private ChromeOptions GetChromeOptions()
    {
        var chromeOptions = new ChromeOptions();
        chromeOptions.AddUserProfilePreference("download.default_directory", _downloadFolder);
        chromeOptions.AddUserProfilePreference("download.prompt_for_download", false);
        chromeOptions.AddUserProfilePreference("download.directory_upgrade", true);
        chromeOptions.AddUserProfilePreference("safebrowsing.enabled", true);
        chromeOptions.AddUserProfilePreference("profile.default_content_setting_values.images", 2);
        chromeOptions.AddUserProfilePreference("profile.managed_default_content_settings.images", 2);

        SetHeadlessViewport(chromeOptions); // <-- use the helper above
        chromeOptions.AddArgument("--remote-debugging-address=127.0.0.1");
        chromeOptions.AddArgument("--disable-background-networking");
        chromeOptions.AddArgument("--disable-background-timer-throttling");
        chromeOptions.AddArgument("--disable-breakpad");
        chromeOptions.AddArgument("--disable-component-update");
        chromeOptions.AddArgument("--disable-default-apps");
        chromeOptions.AddArgument("--disable-extensions");
        chromeOptions.AddArgument("--disable-features=Translate,BackForwardCache,OptimizationHints,MediaRouter");
        chromeOptions.AddArgument("--disable-renderer-backgrounding");
        chromeOptions.AddArgument("--disable-sync");
        chromeOptions.AddArgument("--metrics-recording-only");
        chromeOptions.AddArgument("--mute-audio");
        chromeOptions.AddArgument("--no-first-run");
        chromeOptions.AddArgument("--password-store=basic");
        chromeOptions.AddArgument("--use-mock-keychain");
        chromeOptions.AddArgument("--blink-settings=imagesEnabled=false");
        chromeOptions.AddArgument("--js-flags=--max-old-space-size=128");

        return chromeOptions;
    }

    private static void ConfigureAiScoreBrowserOptions(ChromeOptions chromeOptions)
    {
        chromeOptions.AddArgument("--disable-blink-features=AutomationControlled");
        chromeOptions.AddExcludedArgument("enable-automation");
        chromeOptions.AddAdditionalOption("useAutomationExtension", false);
        chromeOptions.AddArgument("--user-agent=Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36");
        chromeOptions.AddArgument("--disable-gpu");
    }

    private static void ConfigureSofaScoreBrowserOptions(ChromeOptions chromeOptions)
    {
        chromeOptions.AddArgument("--disable-gpu");
        chromeOptions.AddArgument("--lang=en-US");
        chromeOptions.AddArgument("--user-agent=Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
    }

    
    private static TennisSetScoreSummary BuildSetScoreSummary(string score)
    {
        return BuildSetScoreSummary(score, isLive: false);
    }

    private static TennisSetScoreSummary BuildSetScoreSummary(string score, bool isLive)
    {
        return TryParseSetScore(score, isLive, out var homeSetsWon, out var awaySetsWon)
            ? new TennisSetScoreSummary($"{homeSetsWon}:{awaySetsWon}", homeSetsWon, awaySetsWon)
            : new TennisSetScoreSummary(score, null, null);
    }

    private static bool TryParseSetScore(string? score, out int homeSetsWon, out int awaySetsWon)
    {
        return TryParseSetScore(score, isLive: false, out homeSetsWon, out awaySetsWon);
    }

    private static bool TryParseSetScore(string? score, bool isLive, out int homeSetsWon, out int awaySetsWon)
    {
        homeSetsWon = 0;
        awaySetsWon = 0;

        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var scorePairs = ScorePairRegex().Matches(score);
        if (scorePairs.Count == 0)
        {
            return false;
        }

        if (scorePairs.Count == 1)
        {
            var directParts = scorePairs[0].Value.Split(':', StringSplitOptions.TrimEntries);
            if (directParts.Length == 2 &&
                int.TryParse(directParts[0], out var directHome) &&
                int.TryParse(directParts[1], out var directAway))
            {
                if (directHome <= 5 && directAway <= 5)
                {
                    homeSetsWon = directHome;
                    awaySetsWon = directAway;
                    return true;
                }

                if (LooksLikeCompletedTennisSet(directHome, directAway))
                {
                    homeSetsWon = directHome > directAway ? 1 : 0;
                    awaySetsWon = directAway > directHome ? 1 : 0;
                    return true;
                }
            }

            return false;
        }

        foreach (Match scorePair in scorePairs)
        {
            var parts = scorePair.Value.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var setHomeGames) ||
                !int.TryParse(parts[1], out var setAwayGames) ||
                !LooksLikeCompletedTennisSet(setHomeGames, setAwayGames))
            {
                continue;
            }

            if (setHomeGames > setAwayGames)
            {
                homeSetsWon += 1;
            }
            else if (setAwayGames > setHomeGames)
            {
                awaySetsWon += 1;
            }
        }

        if (homeSetsWon + awaySetsWon == 0 && isLive)
        {
            return false;
        }

        return homeSetsWon + awaySetsWon > 0;
    }

    private static bool LooksLikeCompletedTennisSet(int homeGames, int awayGames)
    {
        var winnerGames = Math.Max(homeGames, awayGames);
        var loserGames = Math.Min(homeGames, awayGames);

        if (winnerGames == loserGames)
        {
            return false;
        }

        if (winnerGames == 7 && (loserGames == 5 || loserGames == 6))
        {
            return true;
        }

        return winnerGames >= 6 && winnerGames - loserGames >= 2;
    }
    
    private DateTime ParseScoreMatchTime(string rawTime, bool isLive)
    {
        return ParseScoreMatchTime(rawTime, isLive, DateTimeProvider.GetLocalDate(), applyCurrentDateRolloverHeuristic: true);
    }

    private DateTime ParseScoreMatchTime(
        string rawTime,
        bool isLive,
        DateOnly targetLocalDate,
        bool applyCurrentDateRolloverHeuristic)
    {
        if (isLive)
        {
            return DateTime.UtcNow;
        }

        var extractedTime = ExtractClockTime(rawTime);
        var parsedLocal = DateTime.ParseExact(
            $"{targetLocalDate:dd-MM-yyyy} {extractedTime}",
            "dd-MM-yyyy HH:mm",
            CultureInfo.InvariantCulture);

        if (applyCurrentDateRolloverHeuristic)
        {
            var nowLocal = DateTimeProvider.GetLocalTime();

            // FlashScore's mobile summary mixes prior-day finished rows with today's slate.
            if (parsedLocal > nowLocal.AddHours(2))
            {
                parsedLocal = parsedLocal.AddDays(-1);
            }
        }

        return DateTimeProvider.ConvertLocalToUtc(parsedLocal);
    }

    private async Task<string> FetchFlashScoreTennisHtmlViaBrowserAsync(int dayOffset)
    {
        var downloadUrl = ResolveFlashScoreTennisUrl(_configuration["ScrapingValues:ScoresWebsite"], dayOffset);

        _logger.LogInformation("Fetching FlashScore tennis scores via Chrome from {Url}.", downloadUrl);

        return await RunWithChromeSessionAsync(
            async driver =>
            {
                await driver.Navigate().GoToUrlAsync(downloadUrl);
                WaitForDocumentReady(driver);
                await Task.Delay(500);

                var pageSource = driver.PageSource;
                if (string.IsNullOrWhiteSpace(pageSource) ||
                    !pageSource.Contains("score-data", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"FlashScore browser page source did not include score-data for {downloadUrl}.");
                }

                return pageSource;
            },
            configureOptions: options =>
            {
                options.AddArgument("--window-size=430,932");
                options.AddArgument("--user-agent=Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1");
            },
            purpose: "FlashScore tennis score scraping");
    }

    private List<MatchScore> ParseFlashScoreTennisHtml(string html, int dayOffset)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var container = doc.DocumentNode.SelectSingleNode("//div[@id='score-data']")
            ?? throw new InvalidOperationException("FlashScore tennis HTML did not contain div#score-data.");

        var targetLocalDate = DateTimeProvider.GetLocalDate().AddDays(dayOffset);
        return ParseFlashScoreScoreDataHtml(
            container.InnerHtml,
            targetLocalDate,
            applyCurrentDateRolloverHeuristic: dayOffset == 0);
    }

    private List<MatchScore> ParseFlashScoreScoreDataHtml(
        string scoreDataHtml,
        DateOnly targetLocalDate,
        bool applyCurrentDateRolloverHeuristic)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml($"<div>{scoreDataHtml}</div>");

        var root = doc.DocumentNode.FirstChild;
        if (root is null)
        {
            return [];
        }

        var currentLeague = string.Empty;
        var nodes = root.ChildNodes.ToList();
        var matchScores = new List<MatchScore>();

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];

            switch (node.Name)
            {
                case "h4":
                    currentLeague = NormalizeFlashScoreText(node.InnerText).Split("Standings")[0].Trim();
                    break;
                case "span":
                {
                    var currentTime = NormalizeFlashScoreText(node.InnerText);
                    var className = node.GetAttributeValue("class", string.Empty);
                    var isLive = className.Contains("live", StringComparison.OrdinalIgnoreCase);

                    string? teams = null;
                    string? score = null;

                    for (var j = 1; j <= 5 && i + j < nodes.Count; j++)
                    {
                        var next = nodes[i + j];

                        if (next.Name == "#text")
                        {
                            var text = NormalizeFlashScoreText(next.InnerText);
                            if (string.IsNullOrWhiteSpace(teams) && text.Contains(" - ", StringComparison.Ordinal))
                            {
                                teams = text;
                            }

                            if (string.IsNullOrWhiteSpace(score) && TryExtractFlashScoreScoreText(text, out var extractedScore))
                            {
                                score = extractedScore;
                            }

                            continue;
                        }

                        if (next.Name != "a")
                        {
                            continue;
                        }

                        var nextClass = next.GetAttributeValue("class", string.Empty);
                        if (nextClass is "fin" or "live" || isLive || string.IsNullOrWhiteSpace(nextClass))
                        {
                            var text = NormalizeFlashScoreText(next.InnerText);
                            if (string.IsNullOrWhiteSpace(score) && TryExtractFlashScoreScoreText(text, out var extractedScore))
                            {
                                score = extractedScore;
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(score) || string.IsNullOrWhiteSpace(teams) || !teams.Contains(" - ", StringComparison.Ordinal))
                    {
                        break;
                    }

                    var split = teams.Split(" - ", 2, StringSplitOptions.TrimEntries);
                    if (split.Length != 2)
                    {
                        break;
                    }

                    DateTime matchTime;
                    try
                    {
                        matchTime = ParseScoreMatchTime(currentTime, isLive, targetLocalDate, applyCurrentDateRolloverHeuristic);
                    }
                    catch
                    {
                        matchTime = DateTime.UtcNow;
                    }

                    var scoreSummary = BuildSetScoreSummary(score, isLive);

                    matchScores.Add(new MatchScore
                    {
                        League = currentLeague,
                        HomeTeam = split[0],
                        AwayTeam = split[1],
                        Score = score,
                        NormalizedScoreline = scoreSummary.NormalizedScoreline,
                        HomeSetsWon = scoreSummary.HomeSetsWon,
                        AwaySetsWon = scoreSummary.AwaySetsWon,
                        MatchTime = matchTime,
                        IsLive = isLive
                    });

                    break;
                }
            }
        }

        return matchScores;
    }

    private static string NormalizeFlashScoreText(string? value)
    {
        var decoded = HtmlEntity.DeEntitize(value ?? string.Empty) ?? string.Empty;
        return string.Join(
            " ",
            decoded
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool TryExtractFlashScoreScoreText(string rawText, out string? score)
    {
        score = null;

        var matches = ScorePairRegex().Matches(rawText ?? string.Empty);
        if (matches.Count == 0)
        {
            return false;
        }

        score = string.Join(", ", matches.Select(match => NormalizeScorePair(match.Value)));
        return true;
    }

    private static string NormalizeScorePair(string value)
    {
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? $"{parts[0]}:{parts[1]}" : value.Trim();
    }

    private static string ResolveFlashScoreTennisUrl(string? configuredUrl, int dayOffset = 0)
    {
        var baseUrl = ResolveTennisEndpoint(
            configuredUrl,
            "https://www.flashscore.mobi/tennis",
            "flashscore.mobi",
            "/tennis");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return baseUrl;
        }

        var builder = new UriBuilder(uri);
        var queryParameters = ParseQueryParameters(builder.Query);

        if (dayOffset == 0)
        {
            queryParameters.Remove("d");
        }
        else
        {
            queryParameters["d"] = dayOffset.ToString(CultureInfo.InvariantCulture);
        }

        builder.Query = BuildQueryString(queryParameters);
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string ResolveAiScoreTennisUrl(string? configuredUrl)
    {
        return ResolveTennisEndpoint(
            configuredUrl,
            "https://m.aiscore.com/tennis",
            "aiscore.com",
            "/tennis");
    }

    private static string ResolveTennisScoresBaseUrl(string? configuredUrl)
    {
        return ResolveTennisEndpoint(
            configuredUrl,
            "https://tennisscores.mobi",
            "tennisscores.mobi",
            "/");
    }

    private static string ResolveTennisScoresResultsUrl(string? configuredUrl, DateOnly targetLocalDate)
    {
        var baseUrl = ResolveTennisScoresBaseUrl(configuredUrl);
        var builder = new UriBuilder(baseUrl)
        {
            Path = "/results",
            Query = BuildQueryString(new Dictionary<string, string>
            {
                ["date"] = targetLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            })
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string ResolveTennisEndpoint(
        string? configuredUrl,
        string defaultUrl,
        string expectedHostFragment,
        string requiredPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredUrl)
            ? defaultUrl
            : configuredUrl.Trim();

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return defaultUrl;
        }

        var builder = new UriBuilder(uri);
        if (builder.Host.Contains(expectedHostFragment, StringComparison.OrdinalIgnoreCase) &&
            !builder.Path.StartsWith(requiredPath, StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = requiredPath;
            builder.Query = string.Empty;
        }

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static Dictionary<string, string> ParseQueryParameters(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return parameters;
        }

        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            parameters[key] = value;
        }

        return parameters;
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "&",
            parameters.Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static string ExtractClockTime(string rawTime)
    {
        var match = ClockRegex().Match(rawTime ?? string.Empty);
        if (!match.Success)
        {
            throw new FormatException($"Could not extract a kickoff time from '{rawTime}'.");
        }

        return match.Value;
    }

    [GeneratedRegex(@"\d{1,2}\s*:\s*\d{1,2}")]
    private static partial Regex ScorePairRegex();

    [GeneratedRegex(@"\d{1,2}:\d{2}")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"^\d{1,2}:\d{2}\s+\d{2}/\d{2}$")]
    private static partial Regex ClockWithDayMonthRegex();
    
    private static void SetHeadlessViewport(ChromeOptions options)
    {
        options.AddArgument("--headless=new");
        options.AddArgument("--window-size=1280,1400");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
    }

    private async Task RunWithChromeSessionAsync(
        Func<ChromeDriver, Task> work,
        Action<ChromeOptions>? configureOptions = null,
        string purpose = "browser session")
    {
        await RunWithChromeSessionAsync(
            async driver =>
            {
                await work(driver);
                return true;
            },
            configureOptions,
            purpose);
    }

    private async Task<T> RunWithChromeSessionAsync<T>(
        Func<ChromeDriver, Task<T>> work,
        Action<ChromeOptions>? configureOptions = null,
        string purpose = "browser session")
    {
        var lockAcquired = false;
        ChromeDriverService? service = null;
        ChromeDriver? driver = null;

        try
        {
            _logger.LogDebug("Waiting for the shared Chrome session gate for {Purpose}.", purpose);
            if (!await ChromeSessionGate.WaitAsync(ChromeSessionAcquireTimeout))
            {
                throw new TimeoutException($"Timed out waiting for the shared Chrome session gate for {purpose}.");
            }

            lockAcquired = true;
            service = CreateChromeDriverService();
            var chromeOptions = GetChromeOptions();
            configureOptions?.Invoke(chromeOptions);

            driver = new ChromeDriver(service, chromeOptions, ChromeDriverCommandTimeout);
            return await work(driver);
        }
        finally
        {
            if (driver is not null)
            {
                try
                {
                    driver.Quit();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ChromeDriver quit failed during {Purpose}.", purpose);
                }

                try
                {
                    driver.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ChromeDriver dispose failed during {Purpose}.", purpose);
                }
            }

            if (service is not null)
            {
                ForceStopLingeringChromeDriverProcess(service, purpose);
                service.Dispose();
            }

            if (lockAcquired)
            {
                ChromeSessionGate.Release();
            }
        }
    }

    private static ChromeDriverService CreateChromeDriverService()
    {
        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;
        service.InitializationTimeout = TimeSpan.FromSeconds(30);
        return service;
    }

    private void ForceStopLingeringChromeDriverProcess(ChromeDriverService service, string purpose)
    {
        if (service.ProcessId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(service.ProcessId);
            if (process.HasExited)
            {
                return;
            }

            _logger.LogWarning(
                "Force-stopping lingering ChromeDriver process {ProcessId} after {Purpose}.",
                service.ProcessId,
                purpose);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to force-stop lingering ChromeDriver process {ProcessId}.", service.ProcessId);
        }
    }

    private static void WaitForDocumentReady(IWebDriver driver, int sec = 30)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(sec));
        wait.Until(d =>
        {
            try
            {
                var js = (IJavaScriptExecutor)d;
                return string.Equals(js.ExecuteScript("return document.readyState")?.ToString(), "complete", StringComparison.Ordinal);
            }
            catch { return false; }
        });
    }

    /// Searches default content and all iframes (1 level) for the element.
    /// Returns tuple: (element, frameElementOrNull). If frame is not null, caller must switch to it before using the element.
    private static (IWebElement? el, IWebElement? frame) FindInAllFrames(IWebDriver driver, By by, int sec = 30)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(sec));

        // 1) Try in default content
        driver.SwitchTo().DefaultContent();
        try
        {
            var el = wait.Until(d => d.FindElement(by));
            return (el, null);
        }
        catch { /* ignore */ }

        // 2) Try in iframes
        var frames = driver.FindElements(By.TagName("iframe"));
        foreach (var f in frames)
        {
            try
            {
                driver.SwitchTo().DefaultContent();
                driver.SwitchTo().Frame(f);
                var el = wait.Until(d => d.FindElement(by));
                return (el, f);
            }
            catch { /* try next frame */ }
        }

        driver.SwitchTo().DefaultContent();
        return (null, null);
    }

    private static void JsScrollAndClick(IWebDriver driver, IWebElement el)
    {
        var js = (IJavaScriptExecutor)driver;
        js.ExecuteScript("arguments[0].scrollIntoView({block:'center', inline:'center'});", el);
        js.ExecuteScript("arguments[0].click();", el);
    }

    private static void DismissCookieBanners(IWebDriver driver)
    {
        var js = (IJavaScriptExecutor)driver;
        // Try common consent buttons
        var selectors = new[]
        {
            "#onetrust-accept-btn-handler",
            "[data-testid='uc-accept-all-button']",
            "button[aria-label='Accept all']",
            ".fc-cta-consent",
            ".cookie-accept, .cookie-accept-btn"
        };
        foreach (var sel in selectors)
        {
            var els = driver.FindElements(By.CssSelector(sel));
            if (els.Count > 0)
            {
                try { JsScrollAndClick(driver, els[0]); return; } catch { /* ignore */ }
            }
        }
        // Last resort: hide overlays
        js.ExecuteScript("document.querySelectorAll('.overlay,.modal,.cookies,.consent').forEach(e=>e.style.display='none');");
    }
    
    // Finds and clicks an element by CSS/XPath via JS in default doc and all iframes (1-level).
    // Returns true if it clicked something.
    private static bool ClickByJsAcrossFrames(IWebDriver driver, string selector, bool isXPath, int timeoutSec = 30)
    {
        var js = (IJavaScriptExecutor)driver;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSec))
        {
            // 1) Default content
            driver.SwitchTo().DefaultContent();
            if (TryInCurrentContext()) return true;

            // 2) One-level iframes
            var frames = driver.FindElements(By.TagName("iframe"));
            foreach (var frame in frames)
            {
                try
                {
                    driver.SwitchTo().DefaultContent();
                    driver.SwitchTo().Frame(frame);
                    if (TryInCurrentContext()) return true;
                }
                catch { /* try next frame */ }
            }

            Thread.Sleep(300);
        }

        driver.SwitchTo().DefaultContent();
        return false;

        bool TryInCurrentContext()
        {
            var script = isXPath
                ? @"const xp = arguments[0];
                const r = document.evaluate(xp, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                const el = r.singleNodeValue;
                if(!el) return false;
                el.scrollIntoView({block:'center', inline:'center'});
                el.click();
                return true;"
                : @"const sel = arguments[0];
                const el = document.querySelector(sel);
                if(!el) return false;
                el.scrollIntoView({block:'center', inline:'center'});
                el.click();
                return true;";

            try
            {
                return (bool)(js.ExecuteScript(script, selector) ?? throw new InvalidOperationException());
            }
            catch
            {
                return false;
            }
        }
    }

    private HttpClient CreateTennisScoresHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private async Task<string?> FetchTennisScoresHtmlAsync(HttpClient client, string url)
    {
        try
        {
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("tennisscores.mobi returned {StatusCode} for {Url}.", (int)response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch tennisscores.mobi HTML from {Url}.", url);
            return null;
        }
    }

    private List<MatchScore> ParseTennisScoresPageHtml(string html, DateOnly defaultLocalDate, bool resultsPage)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var root = doc.DocumentNode.SelectSingleNode("//div[@id='matchList']//div[contains(@class,'user')]")
            ?? doc.DocumentNode.SelectSingleNode("//div[@id='matchList']")
            ?? throw new InvalidOperationException("tennisscores.mobi HTML did not contain the expected match list container.");
        var flowNodes = root.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' group-title ') or contains(concat(' ', normalize-space(@class), ' '), ' list ')]");

        var rows = new List<MatchScore>();
        var currentLeague = string.Empty;

        if (flowNodes is null)
        {
            return rows;
        }

        foreach (var node in flowNodes.Where(child => child.NodeType == HtmlNodeType.Element))
        {
            var className = node.GetAttributeValue("class", string.Empty);
            if (className.Contains("group-title", StringComparison.OrdinalIgnoreCase))
            {
                currentLeague = NormalizeTennisScoresText(node.SelectSingleNode(".//span[contains(@class,'leaRow')]")?.InnerText);
                continue;
            }

            if (!className.Contains("list", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var statusText = NormalizeTennisScoresText(node.SelectSingleNode(".//div[contains(@class,'barItem')]//span[1]")?.InnerText);
            var timeText = NormalizeTennisScoresText(node.SelectSingleNode(".//span[contains(@class,'matchTime')]")?.InnerText);
            var teamNodes = node.SelectNodes(".//div[contains(@class,'elseTeamName')]");
            var scoreNodes = node.SelectNodes(".//div[contains(@class,'teamScore')]//div[contains(@class,'bigScore')]//span[contains(@class,'tennis-score')]");

            if (teamNodes is null || teamNodes.Count < 2 || scoreNodes is null || scoreNodes.Count < 2)
            {
                continue;
            }

            var homeTeam = NormalizeTennisScoresText(teamNodes[0].InnerText);
            var awayTeam = NormalizeTennisScoresText(teamNodes[1].InnerText);
            if (string.IsNullOrWhiteSpace(homeTeam) || string.IsNullOrWhiteSpace(awayTeam))
            {
                continue;
            }

            var isFinished = resultsPage ||
                             className.Contains("tn-match-end", StringComparison.OrdinalIgnoreCase) ||
                             className.Contains("item_result", StringComparison.OrdinalIgnoreCase) ||
                             statusText.Equals("END", StringComparison.OrdinalIgnoreCase);
            var isLive = !isFinished &&
                         (className.Contains("tn-match-live", StringComparison.OrdinalIgnoreCase) ||
                          LooksLikeTennisScoresLiveStatus(statusText));

            if (!isFinished && !isLive)
            {
                continue;
            }

            if (!TryParseTennisScoresBigScore(scoreNodes[0].InnerText, out var homeSetsWon) ||
                !TryParseTennisScoresBigScore(scoreNodes[1].InnerText, out var awaySetsWon))
            {
                continue;
            }

            DateTime matchTime;
            try
            {
                matchTime = ParseTennisScoresMatchTime(timeText, defaultLocalDate, isLive);
            }
            catch
            {
                matchTime = isLive
                    ? DateTime.UtcNow
                    : DateTimeProvider.ConvertLocalToUtc(defaultLocalDate.ToDateTime(TimeOnly.MinValue));
            }

            rows.Add(new MatchScore
            {
                League = currentLeague,
                HomeTeam = homeTeam,
                AwayTeam = awayTeam,
                Score = $"{homeSetsWon}:{awaySetsWon}",
                NormalizedScoreline = $"{homeSetsWon}:{awaySetsWon}",
                HomeSetsWon = homeSetsWon,
                AwaySetsWon = awaySetsWon,
                MatchTime = matchTime,
                IsLive = isLive
            });
        }

        return rows;
    }

    private static bool TryParseSofaScoreLeadingToken(string token, out DateTime matchTimeUtc, out string statusText)
    {
        matchTimeUtc = DateTime.UtcNow;
        statusText = string.Empty;

        var normalized = NormalizeTennisScoresText(token);
        var match = Regex.Match(
            normalized,
            @"^(?<time>\d{1,2}:\d{2})(?:\s+(?<day>\d{1,2}/\d{1,2}))?(?:\s+(?<status>.+))?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        if (!TimeOnly.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, out var parsedTime))
        {
            return false;
        }

        var localDate = DateTimeProvider.GetLocalDate();
        if (match.Groups["day"].Success)
        {
            var dayMonth = match.Groups["day"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dayMonth.Length == 2 &&
                int.TryParse(dayMonth[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) &&
                int.TryParse(dayMonth[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month))
            {
                localDate = new DateOnly(DateTimeProvider.GetLocalDate().Year, month, day);
            }
        }

        matchTimeUtc = DateTimeProvider.ConvertLocalToUtc(localDate.ToDateTime(parsedTime));
        statusText = NormalizeTennisScoresText(match.Groups["status"].Value);
        return true;
    }

    private static bool LooksLikeSofaScoreStatusToken(string token)
    {
        var normalized = NormalizeTennisScoresText(token).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Contains("set ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("live", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("retired", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("postponed", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("walkover", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("wo", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("interrupted", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("suspended", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSofaScoreLiveStatus(string? token)
    {
        var normalized = NormalizeTennisScoresText(token).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Contains("set ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("live", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseSofaScoreScoreToken(string token, out int homeSetsWon, out int awaySetsWon, out string normalizedScore)
    {
        homeSetsWon = 0;
        awaySetsWon = 0;
        normalizedScore = string.Empty;

        var normalized = NormalizeTennisScoresText(token);
        var match = Regex.Match(normalized, @"^(?<home>\d+)\s*:\s*(?<away>\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["home"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out homeSetsWon) ||
            !int.TryParse(match.Groups["away"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out awaySetsWon))
        {
            return false;
        }

        normalizedScore = $"{homeSetsWon}:{awaySetsWon}";
        return true;
    }

    private static string NormalizeTennisScoresText(string? value)
    {
        var decoded = HtmlEntity.DeEntitize(value ?? string.Empty) ?? string.Empty;
        return string.Join(
            " ",
            decoded
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool LooksLikeTennisScoresLiveStatus(string statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return false;
        }

        return statusText.StartsWith("S", StringComparison.OrdinalIgnoreCase) ||
               statusText.Contains("LIVE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseTennisScoresBigScore(string? rawValue, out int setsWon)
    {
        return int.TryParse(
            NormalizeTennisScoresText(rawValue),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out setsWon);
    }

    private static DateTime ParseTennisScoresMatchTime(string rawTime, DateOnly defaultLocalDate, bool isLive)
    {
        if (isLive)
        {
            return DateTime.UtcNow;
        }

        var normalizedTime = NormalizeTennisScoresText(rawTime);
        if (ClockWithDayMonthRegex().IsMatch(normalizedTime))
        {
            var parsedLocal = DateTime.ParseExact(
                $"{normalizedTime}/{defaultLocalDate.Year}",
                "HH:mm dd/MM/yyyy",
                CultureInfo.InvariantCulture);

            return DateTimeProvider.ConvertLocalToUtc(parsedLocal);
        }

        var extractedTime = ExtractClockTime(normalizedTime);
        return DateTimeProvider.ConvertLocalToUtc(
            defaultLocalDate.ToDateTime(TimeOnly.ParseExact(extractedTime, "HH:mm", CultureInfo.InvariantCulture)));
    }

    private static string BuildTennisScoresRowKey(MatchScore score)
    {
        return string.Join(
            "|",
            NormalizeTennisScoresText(score.League).ToLowerInvariant(),
            NormalizeTennisScoresText(score.HomeTeam).ToLowerInvariant(),
            NormalizeTennisScoresText(score.AwayTeam).ToLowerInvariant(),
            DateTimeProvider.ConvertUtcToLocalDate(score.MatchTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private sealed record TennisSetScoreSummary(string NormalizedScoreline, int? HomeSetsWon, int? AwaySetsWon);

}
