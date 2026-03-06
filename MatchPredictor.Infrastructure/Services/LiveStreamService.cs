using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium;

namespace MatchPredictor.Infrastructure.Services;

public class LiveStream
{
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string AiScoreMatchId { get; set; } = string.Empty;
    public bool IsLive { get; set; }
}

public class LiveStreamService : BackgroundService
{
    private readonly ILogger<LiveStreamService> _logger;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private const string CacheKey = "ActiveLiveStreams";

    public LiveStreamService(ILogger<LiveStreamService> logger, IMemoryCache cache, IConfiguration configuration)
    {
        _logger = logger;
        _cache = cache;
        _configuration = configuration;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Refreshing active live streams from AiScore...");
                var streams = await FetchStreamsAsync(stoppingToken);

                if (streams.Count > 0)
                {
                    _cache.Set(CacheKey, streams, TimeSpan.FromMinutes(5));
                    _logger.LogInformation($"Successfully cached {streams.Count} active streams.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Detailed error while fetching live streams.");
            }

            // Runs every 4 minutes relative to caching
            await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken);
        }
    }

    private async Task<List<LiveStream>> FetchStreamsAsync(CancellationToken cancellationToken)
    {
        var streams = new List<LiveStream>();

        var useHeadless = _configuration.GetValue<bool>("Scraping:UseHeadlessBrowser", false);
        string html = string.Empty;

        if (!useHeadless)
        {
            try
            {
                var response = await _httpClient.GetAsync("https://www.aiscore.com/", cancellationToken);
                response.EnsureSuccessStatusCode();
                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
                _logger.LogWarning("LiveStream Service HTTP extraction failed. Falling back to Browser.");
                html = await FetchHtmlViaBrowserAsync(cancellationToken);
            }
        }
        else
        {
            html = await FetchHtmlViaBrowserAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            return streams;
        }

        var match = Regex.Match(html, @"window\.__NUXT__=(.*?);</script>");
        if (!match.Success) return streams;

        var jsonStr = match.Groups[1].Value;

        try
        {
            var engine = new Engine();
            engine.Execute("var nuxt = " + jsonStr);
            var extractedJson = engine.Evaluate(@"
                JSON.stringify({ 
                    matches: ((nuxt.state || {})['football/home'] || {}).matchesData_matches || [], 
                    teams: ((nuxt.state || {})['football/home'] || {}).matchesData_teams || []
                })
            ").AsString();

            using var doc = JsonDocument.Parse(extractedJson);
            var root = doc.RootElement;
            var matchesArr = root.GetProperty("matches");
            var teamsArr = root.GetProperty("teams");

            var teamsDict = new Dictionary<string, string>();
            foreach (var t in teamsArr.EnumerateArray())
            {
                string id = t.GetProperty("id").GetString() ?? "";
                string name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : t.TryGetProperty("n", out var nn) ? nn.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) teamsDict[id] = name;
            }

            foreach (var m in matchesArr.EnumerateArray())
            {
                try
                {
                    var sid = m.TryGetProperty("statusId", out var sidProp) && sidProp.ValueKind == JsonValueKind.Number ? sidProp.GetInt32() : 0;
                    var isLive = (sid >= 2 && sid <= 7);

                    if (!isLive) continue;

                    string aiScoreMatchId = "";
                    if (m.TryGetProperty("id", out var matchIdProp))
                    {
                        if (matchIdProp.ValueKind == JsonValueKind.String) aiScoreMatchId = matchIdProp.GetString() ?? "";
                        else if (matchIdProp.ValueKind == JsonValueKind.Number) aiScoreMatchId = matchIdProp.GetRawText();
                    }

                    bool hasVideo = false;
                    if (m.TryGetProperty("hasVideo", out var hv))
                    {
                        if (hv.ValueKind == JsonValueKind.True || hv.ValueKind == JsonValueKind.False) 
                            hasVideo = hv.GetBoolean();
                        else if (hv.ValueKind == JsonValueKind.Number) 
                            hasVideo = hv.GetInt32() == 1;
                    }

                    int lmtMode = 0;
                    if (m.TryGetProperty("lmtMode", out var lmt) && lmt.ValueKind == JsonValueKind.Number)
                    {
                        lmtMode = lmt.GetInt32();
                    }

                    bool hasStream = hasVideo || lmtMode == 1;
                    if (!hasStream || string.IsNullOrEmpty(aiScoreMatchId)) continue; // We only care about games with active streams

                    var htId = m.TryGetProperty("homeTeam", out var ht) ? (ht.TryGetProperty("id", out var htiId) ? htiId.GetString() : "") : m.TryGetProperty("homeTeamId", out var hti) ? hti.GetString() : "";
                    var atId = m.TryGetProperty("awayTeam", out var at) ? (at.TryGetProperty("id", out var atiId) ? atiId.GetString() : "") : m.TryGetProperty("awayTeamId", out var ati) ? ati.GetString() : "";

                    var homeName = !string.IsNullOrEmpty(htId) && teamsDict.TryGetValue(htId, out var hn) ? hn : "";
                    var awayName = !string.IsNullOrEmpty(atId) && teamsDict.TryGetValue(atId, out var an) ? an : "";

                    streams.Add(new LiveStream
                    {
                        HomeTeam = homeName,
                        AwayTeam = awayName,
                        AiScoreMatchId = aiScoreMatchId,
                        IsLive = isLive
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed mapping stream entity.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jint parsing failed in LiveStreamService.");
        }

        return streams;
    }

    private async Task<string> FetchHtmlViaBrowserAsync(CancellationToken cancellationToken)
    {
        var chromeOptions = new ChromeOptions();
        chromeOptions.AddArgument("--headless=new");
        chromeOptions.AddArgument("--disable-gpu");
        chromeOptions.AddArgument("--no-sandbox");
        chromeOptions.AddArgument("--disable-dev-shm-usage");
        chromeOptions.AddArgument("user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        
        using var driver = new ChromeDriver(chromeOptions);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);

        try
        {
            driver.Navigate().GoToUrl("https://www.aiscore.com/");
            await Task.Delay(3000, cancellationToken); // Wait for CF JS challenge
            return driver.PageSource;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChromeDriver failed in LiveStreamService.");
            return string.Empty;
        }
        finally
        {
            driver.Quit();
        }
    }
}
