using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace MatchPredictor.Infrastructure;

public partial class WebScraperService : IWebScraperService
{
    private readonly string _downloadFolder;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebScraperService> _logger;

    public WebScraperService(IConfiguration configuration, ILogger<WebScraperService> logger)
    {
        _logger = logger;
        _configuration = configuration;
        var projectDirectory = Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? string.Empty;
        _downloadFolder = Path.Combine(projectDirectory, "Resources");
        Directory.CreateDirectory(_downloadFolder); // Ensure the directory exists
    }
    
    public async Task ScrapeMatchDataAsync()
    {
        try
        {
            var chromeOptions = GetChromeOptions();

            // var service = ChromeDriverService.CreateDefaultService();
            // service.HideCommandPromptWindow = true;

            DeletePreviousFile();

            var downloadUrl = _configuration["ScrapingValues:ScrapingWebsite"] ?? 
                throw new InvalidOperationException("Download URL not configured in appsettings.json");
            
            using var driver = new ChromeDriver(chromeOptions);
            await driver.Navigate().GoToUrlAsync(downloadUrl);

            // ensure page fully loaded first
            WaitForDocumentReady(driver);

            // Accept/hide cookie banners if any (optional but helpful)
            DismissCookieBanners(driver);

            // Choose one: if your selector in config is XPath, set isXPath=true; else false for CSS
            var selector = _configuration["ScrapingValues:PredictionsButtonSelector"]
                           ?? throw new InvalidOperationException("Predictions button selector not configured");
            var isXPath = selector.TrimStart().StartsWith("/") || selector.StartsWith("(."); // crude check

            var clicked = ClickByJsAcrossFrames(driver, selector, isXPath, timeoutSec: 30);
            if (!clicked)
            {
                // Dump for debugging and fail fast
                await File.WriteAllTextAsync("debug.html", driver.PageSource);
                //((ITakesScreenshot)driver).GetScreenshot().SaveAsFile("debug.png", ScreenshotImageFormat.Png);
                throw new WebDriverTimeoutException($"Could not locate/click element by {(isXPath ? "XPath" : "CSS")}: {selector}");
            }

            _logger.LogInformation("Download button clicked successfully.");


            await CheckFileIsDownloaded();
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
            var chromeOptions = GetChromeOptions();

            var downloadUrl = _configuration["ScrapingValues:ScoresWebsite"] ?? 
                              throw new InvalidOperationException("Download URL for scores is not configured in appsettings.json");
            
            using var driver = new ChromeDriver(chromeOptions);
            _logger.LogInformation("Checking URL for scores...");
            await driver.Navigate().GoToUrlAsync(downloadUrl);
            
            _logger.LogInformation("Commencing scrapping for scores in inner HTML...");
            
            // Wait for dynamic content to render
            await Task.Delay(3000);
            
            var container = driver.FindElement(By.Id("score-data"));
            var rawHtml = container.GetAttribute("innerHTML");

            var doc = new HtmlDocument();
            doc.LoadHtml($"<div>{rawHtml}</div>");

            var currentLeague = "";

            // Use direct ChildNodes — NOT recursive Nodes() which flattens the tree
            var nodes = doc.DocumentNode.FirstChild.ChildNodes.ToList();
            
          var matchScores = new List<MatchScore>();

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
                                teams = next.InnerText.Trim();
                            }
                            else if (next.Name == "a")
                            {
                                var cls = next.GetAttributeValue("class", "");
                                // Accept both finished ("fin") and live scores
                                if (cls == "fin" || isLive || cls == "")
                                {
                                    var rawString = next.InnerText.Trim();
                                    var m = MyRegex().Match(rawString);
                                    if (m.Success)
                                        score = m.Value;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(score) && !string.IsNullOrWhiteSpace(teams) && teams.Contains(" - "))
                        {
                            var split = teams.Split(" - ");
                            var home = split[0].Trim();
                            var away = split[1].Trim();

                            DateTime matchTime;
                            try { matchTime = ParseTime(currentTime); }
                            catch { matchTime = DateTime.UtcNow; } // Live matches may not have a parseable time
                            
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
        catch (Exception e)
        {
            _logger.LogError(e, "❌ An error occurred while scraping match score.");
            throw;
        }
    }

    public async Task<List<AiScoreMatchScore>> ScrapeAiScoreMatchScoresAsync()
    {
        var matchScores = new List<AiScoreMatchScore>();

        // ── Primary: Headless browser → extract window.__NUXT__ state from AiScore ──
        try
        {
            matchScores = await ScrapeAiScoreViaBrowserAsync();
            if (matchScores.Count > 0)
            {
                _logger.LogInformation("Scraped {Count} match scores from AiScore (Nuxt state).", matchScores.Count);
                return matchScores;
            }
            _logger.LogWarning("AiScore browser extraction returned 0 matches. Falling back to API-Football.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiScore browser extraction failed. Falling back to API-Football.");
        }

        // ── Fallback: API-Football REST API ──
        try
        {
            matchScores = await FetchFromApiFootballAsync();
            if (matchScores.Count > 0)
            {
                _logger.LogInformation("Fetched {Count} match scores from API-Football (fallback).", matchScores.Count);
                return matchScores;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API-Football fallback also failed.");
        }

        return matchScores;
    }

    /// <summary>
    /// Extracts match scores from AiScore by loading the page in a headless browser
    /// and reading the already-parsed data from window.__NUXT__.state (Vuex store).
    /// </summary>
    private async Task<List<AiScoreMatchScore>> ScrapeAiScoreViaBrowserAsync()
    {
        var chromeOptions = new ChromeOptions();
        chromeOptions.AddArgument("--headless=new");
        chromeOptions.AddArgument("--window-size=1440,900");
        chromeOptions.AddArgument("--no-sandbox");
        chromeOptions.AddArgument("--disable-dev-shm-usage");
        chromeOptions.AddArgument("--disable-blink-features=AutomationControlled");
        chromeOptions.AddExcludedArgument("enable-automation");
        chromeOptions.AddAdditionalOption("useAutomationExtension", false);
        chromeOptions.AddArgument("--user-agent=Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36");
        chromeOptions.AddArgument("--disable-gpu");

        var aiScoreUrl = _configuration["ScrapingValues:AiScoreWebsite"] ?? "https://m.aiscore.com";

        using var driver = new ChromeDriver(chromeOptions);
        var js = (IJavaScriptExecutor)driver;
        js.ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

        _logger.LogInformation("Navigating to AiScore...");
        await driver.Navigate().GoToUrlAsync(aiScoreUrl);

        // Wait for Cloudflare challenge (up to 25s)
        var maxWait = 25;
        var elapsed = 0;
        while (elapsed < maxWait)
        {
            await Task.Delay(2000);
            elapsed += 2;
            var src = driver.PageSource;
            if (!src.Contains("security verification", StringComparison.OrdinalIgnoreCase) &&
                !src.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase) &&
                !src.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Cloudflare passed after {Elapsed}s.", elapsed);
                break;
            }
        }

        // Wait for Nuxt to hydrate and fetch data
        await Task.Delay(5000);
        WaitForDocumentReady(driver);

        // Extract match data directly from window.__NUXT__ Vuex state
        // Key: state['football/home'] (literal slash), props: matchesData_matches (underscored)
        var extractScript = @"
            try {
                var nuxt = window.__NUXT__;
                if (!nuxt || !nuxt.state) return JSON.stringify({error: 'No __NUXT__ state'});

                var fh = nuxt.state['football/home'];
                if (!fh) return JSON.stringify({error: 'No football/home state', keys: Object.keys(nuxt.state).slice(0, 15)});

                var matches = fh.matchesData_matches || fh['matchesData_matches'] || [];
                var teams = fh.matchesData_teams || fh['matchesData_teams'] || [];
                var competitions = fh.matchesData_competitions || fh['matchesData_competitions'] || [];

                var getObjInfo = function(src, id) {
                    if (!src || !id) return {};
                    if (Array.isArray(src)) {
                        for (var k = 0; k < src.length; k++) {
                            if (src[k].id === id || src[k]._id === id) return src[k];
                        }
                        return {};
                    }
                    return src[id] || {};
                };

                var results = [];
                for (var i = 0; i < matches.length; i++) {
                    var m = matches[i];
                    var homeScores = m.homeScores || [];
                    var awayScores = m.awayScores || [];

                    // statusId: 1=Not started, 2=First half, 3=Half-time, 4=Second half, 
                    //           5=Extra time, 7=Penalties, 8=Finished, 9=Finished AET, 10=Finished Pen
                    var sid = m.statusId || 0;
                    var isLive = (sid >= 2 && sid <= 7);
                    var isFinished = (sid >= 8 && sid <= 10);

                    if (!isLive && !isFinished) continue;

                    // homeScores[0] = current total goals
                    var homeGoals = homeScores.length > 0 ? homeScores[0] : null;
                    var awayGoals = awayScores.length > 0 ? awayScores[0] : null;

                    if (homeGoals === null || awayGoals === null) continue;

                    // Team ID can be m.homeTeamId or m.homeTeam.id (nested object)
                    var htId = m.homeTeamId || (m.homeTeam && m.homeTeam.id) || '';
                    var atId = m.awayTeamId || (m.awayTeam && m.awayTeam.id) || '';
                    // Competition ID
                    var cId = m.competitionId || (m.competition && m.competition.id) || (m.competition && m.competition.competitionId) || '';

                    var homeTeamObj = getObjInfo(teams, htId);
                    var awayTeamObj = getObjInfo(teams, atId);
                    var compObj = getObjInfo(competitions, cId);

                    results.push({
                        home: homeTeamObj.name || homeTeamObj.n || '',
                        away: awayTeamObj.name || awayTeamObj.n || '',
                        homeGoals: homeGoals,
                        awayGoals: awayGoals,
                        league: compObj.name || compObj.n || '',
                        matchTime: m.matchTime || 0,
                        isLive: isLive
                    });
                }
                return JSON.stringify({count: results.length, matches: results});
            } catch(e) {
                return JSON.stringify({error: e.message});
            }
        ";

        var resultJson = js.ExecuteScript(extractScript)?.ToString();
        _logger.LogInformation("AiScore Nuxt extraction result: {Result}",
            resultJson?[..Math.Min(300, resultJson?.Length ?? 0)]);

        if (string.IsNullOrWhiteSpace(resultJson))
            return new List<AiScoreMatchScore>();

        var result = System.Text.Json.JsonDocument.Parse(resultJson).RootElement;

        if (result.TryGetProperty("error", out var err))
        {
            _logger.LogWarning("AiScore extraction error: {Error}", err.GetString());
            return new List<AiScoreMatchScore>();
        }

        var matchScores = new List<AiScoreMatchScore>();
        if (!result.TryGetProperty("matches", out var matchesArr))
            return matchScores;

        foreach (var m in matchesArr.EnumerateArray())
        {
            try
            {
                var homeGoals = m.GetProperty("homeGoals").GetInt32();
                var awayGoals = m.GetProperty("awayGoals").GetInt32();
                var score = $"{homeGoals}:{awayGoals}";

                var matchTimeUnix = m.GetProperty("matchTime").GetInt64();
                var matchTime = matchTimeUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(matchTimeUnix).UtcDateTime
                    : DateTime.UtcNow;

                matchScores.Add(new AiScoreMatchScore
                {
                    League = m.GetProperty("league").GetString() ?? "",
                    HomeTeam = m.GetProperty("home").GetString() ?? "",
                    AwayTeam = m.GetProperty("away").GetString() ?? "",
                    Score = score,
                    MatchTime = matchTime,
                    BTTSLabel = IsBtts(score),
                    IsLive = m.GetProperty("isLive").GetBoolean()
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping AiScore match due to parse error");
            }
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

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("x-apisports-key", apiKey);
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        _logger.LogInformation("Fetching match scores from API-Football for {Date}...", today);
        var response = await httpClient.GetAsync($"{baseUrl}/fixtures?date={today}");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("API-Football returned {Status}.", response.StatusCode);
            return new List<AiScoreMatchScore>();
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            return new List<AiScoreMatchScore>();

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
                var dateStr = fixtureInfo.GetProperty("date").GetString();
                var matchTime = DateTime.TryParse(dateStr, out var parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow;

                matchScores.Add(new AiScoreMatchScore
                {
                    League = league.GetProperty("name").GetString() ?? "",
                    HomeTeam = teams.GetProperty("home").GetProperty("name").GetString() ?? "",
                    AwayTeam = teams.GetProperty("away").GetProperty("name").GetString() ?? "",
                    Score = score,
                    MatchTime = matchTime,
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
    private sealed class AiScoreRawMatch
    {
        public string? Home { get; set; }
        public string? Away { get; set; }
        public string? Score { get; set; }
        public string? Time { get; set; }
        public string? League { get; set; }
        public bool IsLive { get; set; }
    }

    private async Task CheckFileIsDownloaded()
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

        SetHeadlessViewport(chromeOptions); // <-- use the helper above
        chromeOptions.AddArgument("--remote-debugging-address=127.0.0.1");

        return chromeOptions;
    }

    
    private static bool IsBtts(string score)
    {
        var parts = score.Split(":"); // Split "2:1" into ["2", "1"]
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var h) && // Convert "2" to integer h = 2
               int.TryParse(parts[1], out var a) && // Convert "1" to integer a = 1
               h > 0 && a > 0; // Check that both teams scored
    }
    
    private DateTime ParseTime(string time)
    {
        var today = DateTime.UtcNow.Date;
        var parsed = DateTime.ParseExact($"{today:dd-MM-yyyy} {time}", "dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    [GeneratedRegex(@"^\d{1,2}:\d{1,2}")]
    private static partial Regex MyRegex();
    
    private static void SetHeadlessViewport(ChromeOptions options)
    {
        options.AddArgument("--headless=new");
        options.AddArgument("--window-size=1440,2400");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
    }

    private static void WaitForDocumentReady(IWebDriver driver, int sec = 30)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(sec));
        wait.Until(d =>
        {
            try
            {
                var js = (IJavaScriptExecutor)d;
                return (string)js.ExecuteScript("return document.readyState") == "complete";
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