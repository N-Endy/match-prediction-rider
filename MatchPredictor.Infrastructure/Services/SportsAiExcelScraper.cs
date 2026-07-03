using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public interface ISportsAiExcelScraper
{
    Task ScrapeMatchDataAsync();
}

public sealed class SportsAiExcelScraper : ISportsAiExcelScraper
{
    private const string DefaultPredictionsExcelDownloadUrl = "https://www.sports-ai.dev/api/generate-excel";

    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly string _downloadFolder;

    public SportsAiExcelScraper(IConfiguration configuration, ILogger<SportsAiExcelScraper> logger)
        : this(configuration, (ILogger)logger)
    {
    }

    public SportsAiExcelScraper(IConfiguration configuration, ILogger logger)
    {
        _configuration = configuration;
        _logger = logger;
        _downloadFolder = ResolveDownloadFolder();

        Directory.CreateDirectory(_downloadFolder);
    }

    public async Task ScrapeMatchDataAsync()
    {
        try
        {
            DeletePreviousFile();

            var downloadUrl = _configuration["ScrapingValues:PredictionsFileDownloadUrl"]
                              ?? DefaultPredictionsExcelDownloadUrl;
            await DownloadPredictionsExcelAsync(downloadUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while scraping match data.");
            throw;
        }
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

    private async Task DownloadPredictionsExcelAsync(string downloadUrl)
    {
        var fileName = _configuration["ScrapingValues:PredictionsFileName"]
                       ?? throw new InvalidOperationException("Predictions file name not configured in appsettings.json");
        var targetPath = Path.Combine(_downloadFolder, fileName);
        var referringPage = _configuration["ScrapingValues:ScrapingWebsite"] ?? "https://www.sports-ai.dev/predictions";

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,application/octet-stream;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Referrer = new Uri(referringPage);

        _logger.LogInformation("Downloading predictions Excel directly from {DownloadUrl}.", downloadUrl);

        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException($"Predictions download from {downloadUrl} returned an empty response.");
        }

        if (!LooksLikeExcelFile(bytes))
        {
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
            throw new InvalidOperationException($"Predictions download from {downloadUrl} did not return an Excel file. Content-Type: {contentType}.");
        }

        await File.WriteAllBytesAsync(targetPath, bytes);
        _logger.LogInformation("Predictions Excel saved to {TargetPath}.", targetPath);
    }

    private void DeletePreviousFile()
    {
        if (!Directory.Exists(_downloadFolder)) return;

        foreach (var filePath in Directory.GetFiles(_downloadFolder))
        {
            var fileName = Path.GetFileName(filePath);

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

    private static string ResolveDownloadFolder()
    {
        var baseDirFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
        var currentDirFolder = Path.Combine(Directory.GetCurrentDirectory(), "Resources");
        var parentDirFolder = Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? string.Empty, "Resources");

        if (Directory.Exists(baseDirFolder) || AppDomain.CurrentDomain.BaseDirectory.Contains("publish") || AppDomain.CurrentDomain.BaseDirectory.Contains("bin"))
            return baseDirFolder;
        if (Directory.Exists(currentDirFolder))
            return currentDirFolder;

        return parentDirFolder;
    }

    private static bool LooksLikeExcelFile(byte[] bytes)
    {
        return bytes.Length >= 4 &&
               bytes[0] == 0x50 &&
               bytes[1] == 0x4B &&
               bytes[2] == 0x03 &&
               bytes[3] == 0x04;
    }
}
