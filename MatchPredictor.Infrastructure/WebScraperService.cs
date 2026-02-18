using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
            var container = driver.FindElement(By.Id("score-data"));
            var rawHtml = container.GetAttribute("innerHTML");

            var doc = new HtmlDocument();
            doc.LoadHtml($"<div>{rawHtml}</div>");

            var currentLeague = "";

            var nodes = doc.DocumentNode.ChildNodes.Nodes().ToList();
            
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

                        // Next node is the teams (text)
                        var teamsNode = nodes[i + 1];
                        var currentTeams = teamsNode.InnerText.Trim();

                        // Next node may be <a class="fin"> with score
                        string? score = null;

                        for (var j = 2; j <= 3; j++)
                        {
                            if (i + j < nodes.Count && nodes[i + j].Name == "a" && nodes[i + j].GetAttributeValue("class", "") == "fin")
                            {
                                var rawString = nodes[i + j].InnerText.Trim();
                                score = MyRegex().Match(rawString).Value;
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(score) && currentTeams.Contains(" - "))
                        {
                            var split = currentTeams.Split(" - ");
                            var home = split[0].Trim();
                            var away = split[1].Trim();
                            
                            matchScores.Add(new MatchScore
                            {
                                League = currentLeague,
                                HomeTeam = home,
                                AwayTeam = away,
                                Score = score,
                                MatchTime = ParseTime(currentTime),
                                BTTSLabel = IsBtts(score)
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
            _logger.LogError(e, "An error occurred while scraping match score.");
            throw;
        }
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
        var today = DateTime.Today;
        return DateTime.ParseExact($"{today:dd-MM-yyyy} {time}", "dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2}")]
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
                return (bool)js.ExecuteScript(script, selector);
            }
            catch
            {
                return false;
            }
        }
    }

}