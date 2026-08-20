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
    private const int DefaultSofaScoreMaxEventPagesPerRun = 24;
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
    private readonly FlashScoreSourceHealthTracker _flashScoreSourceHealthTracker;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly bool _browserScrapingEnabled;
    private readonly ISportsAiExcelScraper _sportsAiExcelScraper;

    public WebScraperService(
        IConfiguration configuration,
        ILogger<WebScraperService> logger,
        AiScoreSourceHealthTracker aiScoreSourceHealthTracker,
        SofaScoreSourceHealthTracker sofaScoreSourceHealthTracker,
        FlashScoreSourceHealthTracker? flashScoreSourceHealthTracker = null,
        IHttpClientFactory? httpClientFactory = null,
        ISportsAiExcelScraper? sportsAiExcelScraper = null)
    {
        _logger = logger;
        _configuration = configuration;
        _aiScoreSourceHealthTracker = aiScoreSourceHealthTracker;
        _sofaScoreSourceHealthTracker = sofaScoreSourceHealthTracker;
        _flashScoreSourceHealthTracker = flashScoreSourceHealthTracker ?? new FlashScoreSourceHealthTracker();
        _httpClientFactory = httpClientFactory;
        _browserScrapingEnabled = ResolveBrowserScrapingEnabled(configuration);
        _sportsAiExcelScraper = sportsAiExcelScraper ?? new SportsAiExcelScraper(configuration, logger);
        
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
        await _sportsAiExcelScraper.ScrapeMatchDataAsync();
    }

    public async Task<List<MatchScore>> ScrapeMatchScoresAsync()
    {
        EnsureBrowserScrapingEnabled("primary score scraping");

        // Fast-skip the primary scraper while its circuit is open (e.g. after repeated Cloudflare
        // blocks) so a single failing source can't stall every score-update cycle.
        if (_flashScoreSourceHealthTracker.IsInCooldown(DateTime.UtcNow, out var cooldownRemaining))
        {
            _logger.LogWarning(
                "FlashScore primary scraper is in cooldown for another {Seconds:F0}s; skipping this cycle.",
                cooldownRemaining.TotalSeconds);
            return [];
        }

        _flashScoreSourceHealthTracker.RecordAttempt("browser");

        try
        {
            var scores = await RunWithChromeSessionAsync(
                async driver =>
                {
                    var downloadUrl = _configuration["ScrapingValues:ScoresWebsite"] ??
                                      throw new InvalidOperationException("Download URL for scores is not configured in appsettings.json");

                    _logger.LogInformation("Checking URL for scores...");

                    var js = (IJavaScriptExecutor)driver;
                    js.ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

                    await driver.Navigate().GoToUrlAsync(downloadUrl);

                    var pageSource = driver.PageSource;
                    var title = driver.Title;
                    if (LooksLikeSofaScoreChallengePage(pageSource, title))
                    {
                        _logger.LogWarning("Primary scraper browser was blocked by a challenge page ('{Title}').", title);
                        throw new WebDriverException($"Blocked by Cloudflare challenge page: {title}");
                    }

                    _logger.LogInformation("Commencing scrapping for scores in inner HTML...");

                    var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(15));
                    IWebElement container;
                    try
                    {
                        container = wait.Until(d => d.FindElement(By.Id("score-data")));
                    }
                    catch (WebDriverTimeoutException)
                    {
                        pageSource = driver.PageSource;
                        title = driver.Title;
                        if (LooksLikeSofaScoreChallengePage(pageSource, title))
                        {
                            _logger.LogWarning("Primary scraper browser was blocked by a challenge page ('{Title}') during wait.", title);
                            throw new WebDriverException($"Blocked by Cloudflare challenge page: {title}");
                        }
                        throw;
                    }

                    var rawHtml = container.GetAttribute("innerHTML");

                    return ParseScoreDataHtml(rawHtml);
                },
                configureOptions: ConfigurePrimaryScraperBrowserOptions,
                purpose: "score scraping");

            _flashScoreSourceHealthTracker.RecordSuccess("browser", scores.Count);
            return scores;
        }
        catch (Exception e)
        {
            _flashScoreSourceHealthTracker.RecordFailure("browser", e.Message);
            _logger.LogError(e, "❌ An error occurred while scraping match score.");
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
            return await FetchAndTrackApiFootballFallbackAsync("AiScore cooldown active.");
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
            var detail = "Browser scraping is disabled. Skipping AiScore headless browser fallback and using API-Football.";
            _aiScoreSourceHealthTracker.RecordAttempt("browser-disabled", detail);
            _logger.LogWarning("{Detail}", detail);
            return await FetchAndTrackApiFootballFallbackAsync("Browser scraping disabled.");
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
                _logger.LogWarning("{Detail} Falling back to API-Football.", browserAttempt.Detail);
            }
            else
            {
                _aiScoreSourceHealthTracker.RecordEmpty("browser", browserAttempt.Detail);
                _logger.LogWarning("AiScore Browser extraction returned 0 matches. Falling back to API-Football.");
            }
        }
        catch (Exception ex)
        {
            _aiScoreSourceHealthTracker.RecordFailure("browser", ex.Message);
            _logger.LogWarning(ex, "AiScore Browser extraction failed. Falling back to API-Football.");
        }

        return await FetchAndTrackApiFootballFallbackAsync("AiScore unavailable after direct attempts.");
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

        PruneExpiredSofaScoreEventUrlCache(DateTime.UtcNow);

        try
        {
            using var client = CreateSofaScoreHttpClient();

            if (_browserScrapingEnabled)
            {
                _sofaScoreSourceHealthTracker.RecordAttempt("browser-discovery", $"Targeted fixtures: {requestedFixtures.Count}.");

                var browserAttempt = await ScrapeSofaScoreViaBrowserCrawlerAsync(requestedFixtures);
                if (browserAttempt.Scores.Count > 0)
                {
                    _logger.LogInformation(
                        "Scraped {Count} SofaScore browser-crawled score rows for {FixtureCount} incomplete fixtures.",
                        browserAttempt.Scores.Count,
                        requestedFixtures.Count);
                    _sofaScoreSourceHealthTracker.RecordSuccess(
                        browserAttempt.Stage,
                        browserAttempt.Scores.Count,
                        browserAttempt.CandidateCount,
                        browserAttempt.DetailFetchCount,
                        browserAttempt.Detail);
                    return browserAttempt.Scores;
                }

                _logger.LogInformation("{Detail}", browserAttempt.Detail);
            }
            else
            {
                var detail = $"Browser scraping is disabled. Skipping SofaScore browser crawler for {requestedFixtures.Count} targeted fixture(s) and using HTML fallback only.";
                _sofaScoreSourceHealthTracker.RecordAttempt("html-only", detail);
                _logger.LogInformation("{Detail}", detail);
            }

            return await ScrapeSofaScoreViaHtmlFallbackAsync(requestedFixtures, client);
        }
        catch (Exception ex)
        {
            _sofaScoreSourceHealthTracker.RecordFailure("runtime", ex.Message);
            _logger.LogWarning(ex, "SofaScore targeted scraping failed.");
            return [];
        }
    }

    /// <summary>
    /// Extracts match scores from AiScore by downloading the HTML via HttpClient.
    /// Fast, but might get blocked by Cloudflare (403 Forbidden).
    /// </summary>
    private async Task<AiScoreAttemptResult> ScrapeAiScoreViaHttpAttemptAsync()
    {
        var aiScoreUrl = _configuration["ScrapingValues:AiScoreWebsite"] ?? "https://m.aiscore.com";

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
        var aiScoreUrl = _configuration["ScrapingValues:AiScoreWebsite"] ?? "https://m.aiscore.com";

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
                    var s = window.__NUXT__ && window.__NUXT__.state && window.__NUXT__.state['football/home'];
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

                    var homeGoals = homeScores[0].GetInt32();
                    var awayGoals = awayScores[0].GetInt32();

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

                    var matchId = m.TryGetProperty("id", out var midProp)
                        ? midProp.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? midProp.GetRawText()
                            : midProp.GetString()
                        : m.TryGetProperty("matchId", out var matchIdProp)
                            ? matchIdProp.ValueKind == System.Text.Json.JsonValueKind.Number
                                ? matchIdProp.GetRawText()
                                : matchIdProp.GetString()
                            : null;

                    var score = $"{homeGoals}:{awayGoals}";
                    matchScores.Add(new AiScoreMatchScore
                    {
                        League = leagueName,
                        HomeTeam = homeName,
                        AwayTeam = awayName,
                        Score = score,
                        MatchTime = matchTime,
                        SourceEventId = string.IsNullOrWhiteSpace(matchId) ? null : matchId,
                        HomeTeamId = string.IsNullOrWhiteSpace(htId) ? null : htId,
                        AwayTeamId = string.IsNullOrWhiteSpace(atId) ? null : atId,
                        BTTSLabel = IsBtts(score),
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
                    matches: (nuxt.state['football/home'] || {}).matchesData_matches || [], 
                    teams: (nuxt.state['football/home'] || {}).matchesData_teams || [], 
                    comps: (nuxt.state['football/home'] || {}).matchesData_competitions || [] 
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

                    var homeGoals = homeScores[0].GetInt32();
                    var awayGoals = awayScores[0].GetInt32();
                    
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

                    var score = $"{homeGoals}:{awayGoals}";

                    var matchId = m.TryGetProperty("id", out var midProp)
                        ? midProp.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? midProp.GetRawText()
                            : midProp.GetString()
                        : m.TryGetProperty("matchId", out var matchIdProp)
                            ? matchIdProp.ValueKind == System.Text.Json.JsonValueKind.Number
                                ? matchIdProp.GetRawText()
                                : matchIdProp.GetString()
                            : null;

                    matchScores.Add(new AiScoreMatchScore
                    {
                        League = leagueName,
                        HomeTeam = homeName,
                        AwayTeam = awayName,
                        Score = score,
                        MatchTime = matchTime,
                        SourceEventId = string.IsNullOrWhiteSpace(matchId) ? null : matchId,
                        HomeTeamId = string.IsNullOrWhiteSpace(htId) ? null : htId,
                        AwayTeamId = string.IsNullOrWhiteSpace(atId) ? null : atId,
                        BTTSLabel = IsBtts(score),
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

    /// <summary>
    /// Fallback: Fetches scores from API-Football REST API (free tier, 100 req/day).
    /// </summary>
    private async Task<List<AiScoreMatchScore>> FetchFromApiFootballAsync()
    {
        var apiKey = _configuration["ApiFootball:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("API-Football API key not configured. Skipping fallback.");
            return new List<AiScoreMatchScore>();
        }

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var baseUrl = _configuration["ApiFootball:BaseUrl"] ?? "https://v3.football.api-sports.io";

        // Prefer the named, Polly-backed "ApiFootball" client (retry + timeout policies) when an
        // IHttpClientFactory is available; fall back to a plain client for tests/standalone use.
        HttpClient httpClient;
        HttpClient? ownedClient = null;
        if (_httpClientFactory is not null)
        {
            httpClient = _httpClientFactory.CreateClient("ApiFootball");
        }
        else
        {
            ownedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            httpClient = ownedClient;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fixtures?date={today}");
            request.Headers.TryAddWithoutValidation("x-apisports-key", apiKey);

            _logger.LogInformation("Fetching match scores from API-Football for {Date}...", today);
            var response = await httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("API-Football returned {Status}.", response.StatusCode);
            return new List<AiScoreMatchScore>();
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errors))
        {
            if (errors.ValueKind == System.Text.Json.JsonValueKind.Array && errors.GetArrayLength() > 0)
                return new List<AiScoreMatchScore>();
            if (errors.ValueKind == System.Text.Json.JsonValueKind.Object && errors.EnumerateObject().Any())
                return new List<AiScoreMatchScore>();
        }

        var matchScores = new List<AiScoreMatchScore>();
        var fixtures = root.GetProperty("response");

        foreach (var fixture in fixtures.EnumerateArray())
        {
            try
            {
                var fixtureInfo = fixture.GetProperty("fixture");
                var teams = fixture.GetProperty("teams");
                var goals = fixture.GetProperty("goals");
                var league = fixture.GetProperty("league");
                var statusShort = fixtureInfo.GetProperty("status").GetProperty("short").GetString() ?? "";

                var liveStatuses = new HashSet<string> { "1H", "2H", "HT", "ET", "BT", "P" };
                var finishedStatuses = new HashSet<string> { "FT", "AET", "PEN" };

                if (!liveStatuses.Contains(statusShort) && !finishedStatuses.Contains(statusShort))
                    continue;

                var homeGoals = goals.GetProperty("home");
                var awayGoals = goals.GetProperty("away");
                if (homeGoals.ValueKind == System.Text.Json.JsonValueKind.Null ||
                    awayGoals.ValueKind == System.Text.Json.JsonValueKind.Null)
                    continue;

                var score = $"{homeGoals.GetInt32()}:{awayGoals.GetInt32()}";
                var regularTimeScore = ApiFootballScoreParser.TryReadRegularTimeScore(fixture);
                var dateStr = fixtureInfo.GetProperty("date").GetString();
                var matchTime = DateTime.TryParse(dateStr, out var parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow;
                var fixtureId = fixtureInfo.TryGetProperty("id", out var fixtureIdProp)
                    ? fixtureIdProp.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? fixtureIdProp.GetRawText()
                        : fixtureIdProp.GetString()
                    : null;
                var homeTeamId = teams.GetProperty("home").TryGetProperty("id", out var homeIdProp)
                    ? homeIdProp.GetRawText()
                    : null;
                var awayTeamId = teams.GetProperty("away").TryGetProperty("id", out var awayIdProp)
                    ? awayIdProp.GetRawText()
                    : null;

                matchScores.Add(new AiScoreMatchScore
                {
                    League = league.GetProperty("name").GetString() ?? "",
                    HomeTeam = teams.GetProperty("home").GetProperty("name").GetString() ?? "",
                    AwayTeam = teams.GetProperty("away").GetProperty("name").GetString() ?? "",
                    Score = score,
                    RegularTimeScore = regularTimeScore,
                    MatchTime = matchTime,
                    SourceEventId = string.IsNullOrWhiteSpace(fixtureId) ? null : fixtureId,
                    HomeTeamId = string.IsNullOrWhiteSpace(homeTeamId) ? null : homeTeamId,
                    AwayTeamId = string.IsNullOrWhiteSpace(awayTeamId) ? null : awayTeamId,
                    BTTSLabel = IsBtts(score),
                    IsLive = liveStatuses.Contains(statusShort)
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping API-Football fixture");
            }
        }

            _logger.LogInformation("Fetched {Count} from API-Football.", matchScores.Count);
            return matchScores;
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    private async Task<List<AiScoreMatchScore>> FetchAndTrackApiFootballFallbackAsync(string reason)
    {
        try
        {
            var matchScores = await FetchFromApiFootballAsync();
            _aiScoreSourceHealthTracker.RecordFallback(
                "api-football",
                matchScores.Count,
                $"{reason} API-Football returned {matchScores.Count} match(es).");

            if (matchScores.Count > 0)
            {
                _logger.LogInformation("Fetched {Count} match scores from API-Football (fallback).", matchScores.Count);
            }

            return matchScores;
        }
        catch (Exception ex)
        {
            _aiScoreSourceHealthTracker.RecordFailure("api-football", ex.Message);
            _logger.LogWarning(ex, "API-Football fallback also failed.");
            return [];
        }
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
            var listingEntries = SofaScoreListingPageParser.ParseEntries(pageSource, baseUrl);
            var listingScores = ResolveSofaScoreListingScores(fixtures, listingEntries);

            if (listingScores.Count > 0)
            {
                return BuildSofaScoreListingAttempt(
                    listingScores,
                    listingEntries.Count,
                    $"SofaScore browser listing DOM matched {listingScores.Count} targeted fixture(s) from {listingEntries.Count} rendered row(s).");
            }

            return new SofaScoreApiAttemptResult(
                [],
                "browser-listing-dom",
                listingEntries.Count > 0
                    ? $"SofaScore browser listing DOM parsed {listingEntries.Count} rendered row(s) from {listingUrl}, but none matched the targeted fixtures."
                    : $"SofaScore browser listing DOM found no rendered match rows on {listingUrl}.",
                listingEntries.Count,
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
        var eventPageBudget = ParseConfiguredInt("ScrapingValues:SofaScoreMaxEventPagesPerRun", DefaultSofaScoreMaxEventPagesPerRun);
        var pagesFetched = 0;
        var scrapedScores = new List<SofaScoreMatchScore>();

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
                    await driver.Navigate().GoToUrlAsync(candidateUrl);
                    WaitForDocumentReady(driver);
                    DismissCookieBanners(driver);
                    await Task.Delay(1500);

                    var html = driver.PageSource;
                    if (string.IsNullOrWhiteSpace(html) ||
                        !SofaScoreEventPageParser.TryParse(html, candidateUrl, out var parsedScore) ||
                        !SofaScoreEventMatchesFixture(parsedScore, fixture))
                    {
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
                : $"SofaScore browser crawler found {eventUrlsByFixture.Values.Sum(urls => urls.Count)} candidate URL(s) but no targeted event pages parsed successfully.",
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
            $"{baseUrl}/football",
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
                if (string.IsNullOrWhiteSpace(html) ||
                    !SofaScoreEventPageParser.TryParse(html, candidateUrl, out var parsedScore) ||
                    !SofaScoreEventMatchesFixture(parsedScore, fixture))
                {
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
                $"SofaScore HTML fallback found {candidateUrlCount} candidate URL(s) but no matching event pages were parsed successfully.",
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

            resolvedScores.Add(new SofaScoreMatchScore
            {
                League = string.IsNullOrWhiteSpace(bestCandidate.League) ? fixture.League : bestCandidate.League,
                HomeTeam = bestCandidate.HomeTeam,
                AwayTeam = bestCandidate.AwayTeam,
                Score = bestCandidate.Score,
                DisplayedScore = bestCandidate.Score,
                RegularTimeScore = bestCandidate.IsLive ? null : bestCandidate.Score,
                StatusText = bestCandidate.StatusText,
                EventUrl = bestCandidate.EventUrl,
                EventId = TryParseSofaScoreEventId(bestCandidate.EventUrl),
                MatchTime = ResolveSofaScoreListingMatchTimeUtc(fixture, bestCandidate),
                BTTSLabel = IsBtts(bestCandidate.Score),
                IsLive = bestCandidate.IsLive
            });

            remainingCandidates.Remove(bestCandidate);
        }

        return resolvedScores;
    }

    private static long? TryParseSofaScoreEventId(string? eventUrl)
    {
        if (string.IsNullOrWhiteSpace(eventUrl))
        {
            return null;
        }

        var match = Regex.Match(eventUrl, @"/(?:event|api/v1/event)/(?<id>\d+)");
        return match.Success && long.TryParse(match.Groups["id"].Value, out var eventId)
            ? eventId
            : null;
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
                $"SofaScore processed {candidateUrlPool.SitemapsProcessed} sitemap(s) but discovered no football match URLs.");
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
                $"SofaScore discovered {candidateUrls.Count} football event URL(s) across {candidateUrlPool.SitemapsProcessed} sitemap(s) but none matched the {unresolvedFixtures.Count} targeted fixture slug pairs.");
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
                if (entry.Location.Contains("/football/match/", StringComparison.OrdinalIgnoreCase))
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
        var parsedLocalDate = DateTimeProvider.ConvertUtcToLocal(parsedScore.MatchTime).Date;
        var targetLocalDate = fixture.MatchLocalDate.ToDateTime(TimeOnly.MinValue).Date;
        var dateDeltaDays = Math.Abs((parsedLocalDate - targetLocalDate).TotalDays);
        if (dateDeltaDays > 1)
        {
            return false;
        }

        if (!TeamsLookEquivalent(parsedScore.HomeTeam, fixture.HomeTeam) ||
            !TeamsLookEquivalent(parsedScore.AwayTeam, fixture.AwayTeam))
        {
            return false;
        }

        if (!fixture.ScheduledMatchTimeUtc.HasValue)
        {
            return true;
        }

        var kickoffDeltaMinutes = Math.Abs((parsedScore.MatchTime - fixture.ScheduledMatchTimeUtc.Value).TotalMinutes);
        return kickoffDeltaMinutes <= 16 * 60;
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
        if (location.Contains("/football/match/", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (location.Contains("football", StringComparison.OrdinalIgnoreCase))
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
        chromeOptions.AddArgument("--js-flags=--max-old-space-size=512");

        return chromeOptions;
    }

    private static void ConfigurePrimaryScraperBrowserOptions(ChromeOptions chromeOptions)
    {
        chromeOptions.AddArgument("--disable-blink-features=AutomationControlled");
        chromeOptions.AddExcludedArgument("enable-automation");
        chromeOptions.AddAdditionalOption("useAutomationExtension", false);
        chromeOptions.AddArgument("--user-agent=Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36");
        chromeOptions.AddArgument("--disable-gpu");
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

    
    internal static List<MatchScore> ParseScoreDataHtml(string rawHtml)
    {
        var matchScores = new List<MatchScore>();
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return matchScores;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml($"<div>{rawHtml}</div>");

        var currentLeague = "";

        // Use direct ChildNodes — NOT recursive Nodes() which flattens the tree
        var nodes = doc.DocumentNode.FirstChild.ChildNodes.ToList();

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];

            switch (node.Name)
            {
                case "h4":
                    currentLeague = node.InnerText.Split("Standings")[0].Trim();
                    break;
                case "span":
                {
                    var currentTime = node.InnerText.Trim();
                    var isLive = node.GetAttributeValue("class", "") == "live";

                    // Look ahead for teams (text node) and score (a.fin or live score link)
                    string? teams = null;
                    string? score = null;

                    for (var j = 1; j <= 4 && i + j < nodes.Count; j++)
                    {
                        var next = nodes[i + j];

                        if (next.Name == "#text" && next.InnerText.Contains(" - "))
                        {
                            teams = HtmlEntity.DeEntitize(next.InnerText).Trim();
                        }
                        else if (next.Name == "a")
                        {
                            var anchorClass = next.GetAttributeValue("class", "");
                            var isLiveAnchor = anchorClass == "live";
                            // Accept both finished ("fin") and live scores
                            if (anchorClass == "fin" || isLive || isLiveAnchor)
                            {
                                var rawString = HtmlEntity.DeEntitize(next.InnerText).Trim();
                                var m = MyRegex().Match(rawString);
                                if (m.Success)
                                {
                                    // FlashScore mobile renders scores with a hyphen (e.g. "2-1");
                                    // normalize to the colon format the rest of the pipeline expects.
                                    score = $"{m.Groups["home"].Value}:{m.Groups["away"].Value}";
                                    if (isLiveAnchor)
                                    {
                                        isLive = true;
                                    }
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(score) && !string.IsNullOrWhiteSpace(teams) && teams.Contains(" - "))
                    {
                        var split = teams.Split(" - ");
                        var home = split[0].Trim();
                        var away = split[1].Trim();

                        DateTime matchTime;
                        try { matchTime = ParseScoreMatchTime(currentTime, isLive); }
                        catch { matchTime = DateTime.UtcNow; } // Live matches may not expose a kickoff time in the listing

                        matchScores.Add(new MatchScore
                        {
                            League = currentLeague,
                            HomeTeam = home,
                            AwayTeam = away,
                            Score = score,
                            MatchTime = matchTime,
                            BTTSLabel = IsBtts(score),
                            IsLive = isLive
                        });
                    }

                    break;
                }
            }
        }

        return matchScores;
    }

    private static bool IsBtts(string score)
    {
        var parts = score.Split(":"); // Split "2:1" into ["2", "1"]
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var h) && // Convert "2" to integer h = 2
               int.TryParse(parts[1], out var a) && // Convert "1" to integer a = 1
               h > 0 && a > 0; // Check that both teams scored
    }
    
    private static DateTime ParseScoreMatchTime(string rawTime, bool isLive)
    {
        if (isLive)
        {
            return DateTime.UtcNow;
        }

        var nowLocal = DateTimeProvider.GetLocalTime();
        var extractedTime = ExtractClockTime(rawTime);
        var parsedLocal = DateTime.ParseExact(
            $"{nowLocal:dd-MM-yyyy} {extractedTime}",
            "dd-MM-yyyy HH:mm",
            CultureInfo.InvariantCulture);

        // FlashScore's mobile summary mixes prior-day finished rows with today's slate.
        if (parsedLocal > nowLocal.AddHours(2))
        {
            parsedLocal = parsedLocal.AddDays(-1);
        }

        return DateTimeProvider.ConvertLocalToUtc(parsedLocal);
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

    [GeneratedRegex(@"^(?<home>\d{1,2})\s*[-:]\s*(?<away>\d{1,2})")]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"\d{1,2}:\d{2}")]
    private static partial Regex ClockRegex();
    
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
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
            driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
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

}
