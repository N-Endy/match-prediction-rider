using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using OfficeOpenXml;
using System.Globalization;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Infrastructure;

public class ExtractFromExcel : IExtractFromExcel
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExtractFromExcel> _logger;

    public ExtractFromExcel(IConfiguration configuration, ILogger<ExtractFromExcel> logger)
    {
        _configuration = configuration;
        _logger = logger;
        EpplusLicenseBootstrapper.EnsureInitialized(configuration, logger);
    }

    private string GetFilePath()
    {
        var fileName = _configuration["ScrapingValues:PredictionsFileName"] ?? "predictions.xlsx";
        
        var baseDirFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
        var currentDirFolder = Path.Combine(Directory.GetCurrentDirectory(), "Resources");
        var parentDirFolder = Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? string.Empty, "Resources");
        var userProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        string[] searchPaths = 
        {
            Path.Combine(baseDirFolder, fileName),
            Path.Combine(currentDirFolder, fileName),
            Path.Combine(parentDirFolder, fileName),
            Path.Combine(userProfileDir, fileName),
            Path.Combine("/Resources", fileName),
            Path.Combine("Resources", fileName),
            Path.Combine("/app/Resources", fileName) // Common docker structure fallback
        };

        foreach (var path in searchPaths)
        {
            _logger.LogDebug("Probing for Excel file at: {Path} → {Exists}", path, File.Exists(path));
            if (File.Exists(path))
            {
                _logger.LogInformation("✅ Excel file found at: {Path}", path);
                return path;
            }
        }
        _logger.LogWarning("⚠️ Excel file not found in any probed location. Searched: {Paths}", string.Join(", ", searchPaths));

        // If file not found in any probed location, fallback to what WebScraperService ideally evaluates to
        string downloadFolder;
        if (Directory.Exists(baseDirFolder) || AppDomain.CurrentDomain.BaseDirectory.Contains("publish") || AppDomain.CurrentDomain.BaseDirectory.Contains("bin"))
            downloadFolder = baseDirFolder;
        else if (Directory.Exists(currentDirFolder))
            downloadFolder = currentDirFolder;
        else
            downloadFolder = parentDirFolder;

        return Path.Combine(downloadFolder, fileName);
    }
    
    public IEnumerable<MatchData> ExtractMatchDatasetFromFile()
    {
        var extractedData = new List<MatchData>();
        var filePath = GetFilePath();

        try
        {
            // If filePath is not found
            if (!File.Exists(filePath))
            {
                _logger.LogError("❌ Excel file not found at path: {FilePath}", filePath);
                throw new FileNotFoundException("Excel file not found at path: " + filePath);
            }

            // If the Excel file is empty, return
            if (new FileInfo(filePath).Length == 0)
            {
                _logger.LogWarning("Excel file is empty.");
                return extractedData;
            }

            // Read data from the downloaded Excel file and extract relevant information
            using var package = new ExcelPackage(new FileInfo(filePath));

            if (package.Workbook.Worksheets.Count <= 0)
            {
                _logger.LogWarning("❌ Excel file does not contain any worksheets.");
                return extractedData;
            }

            var worksheet = package.Workbook.Worksheets[0];

            if (!string.Equals(worksheet.Name, "soccer", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("❌ Expected worksheet[0] to be 'soccer' but found '{WorksheetName}'.", worksheet.Name);
                throw new InvalidOperationException($"Expected first worksheet to be 'soccer' but found '{worksheet.Name}'.");
            }

            if (worksheet.Dimension == null || worksheet.Dimension.Rows < 2)
            {
                _logger.LogWarning("❌ Excel file is empty.");
                return extractedData;
            }

            ValidateExpectedHeaders(worksheet);

            var rowCount = worksheet.Dimension.Rows;
            var today = DateTimeProvider.GetLocalTime().Date;

            for (var row = 2; row <= rowCount; row++)
            {
                var dateString = worksheet.Cells[row, 5].Value?.ToString();
                if (string.IsNullOrWhiteSpace(dateString))
                    continue;

                if (!DateTime.TryParseExact(
                        dateString,
                        "d.M.yyyy H:mm",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var matchDateTime))
                {
                    _logger.LogDebug("Skipping row {Row} because date '{DateString}' could not be parsed.", row, dateString);
                    continue;
                }

                if (matchDateTime.Date != today)
                    continue;

                var matchData = new MatchData
                {
                    Date = matchDateTime.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                    Time = matchDateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    League = worksheet.Cells[row, 4].Value?.ToString(),
                    HomeTeam = worksheet.Cells[row, 2].Value?.ToString(),
                    AwayTeam = worksheet.Cells[row, 3].Value?.ToString(),
                    HomeWin = ParseProbability(worksheet.Cells[row, 6].Value),
                    Draw = ParseProbability(worksheet.Cells[row, 7].Value),
                    AwayWin = ParseProbability(worksheet.Cells[row, 8].Value),
                    OverOneGoal = ParseProbability(worksheet.Cells[row, 12].Value),
                    OverOnePointFive = ParseProbability(worksheet.Cells[row, 14].Value),
                    OverTwoGoals = ParseProbability(worksheet.Cells[row, 18].Value),
                    OverThreeGoals = ParseProbability(worksheet.Cells[row, 22].Value),
                    OverFourGoals = ParseProbability(worksheet.Cells[row, 24].Value),
                    UnderOnePointFive = ParseProbability(worksheet.Cells[row, 30].Value),
                    UnderTwoGoals = ParseProbability(worksheet.Cells[row, 34].Value),
                    UnderThreeGoals = ParseProbability(worksheet.Cells[row, 38].Value),
                    AhZeroHome = ParseProbability(worksheet.Cells[row, 57].Value),
                    AhZeroAway = ParseProbability(worksheet.Cells[row, 58].Value),
                    AhMinusHalfHome = ParseProbability(worksheet.Cells[row, 53].Value),
                    AhMinusHalfAway = ParseProbability(worksheet.Cells[row, 54].Value),
                    AhMinusOneHome = ParseProbability(worksheet.Cells[row, 49].Value),
                    AhMinusOneAway = ParseProbability(worksheet.Cells[row, 50].Value),
                    AhPlusHalfHome = ParseProbability(worksheet.Cells[row, 71].Value),
                    AhPlusHalfAway = ParseProbability(worksheet.Cells[row, 72].Value)
                };

                matchData.NormalizeSourceProbabilities();
                extractedData.Add(matchData);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "❌ An error occurred while extracting data from Excel file.");
            throw; // Re-throw the exception to handle it further up if needed
        }
        return extractedData;
    }

    private static double ParseProbability(object? value)
    {
        return double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
    }

    private static void ValidateExpectedHeaders(ExcelWorksheet worksheet)
    {
        var requiredHeaders = new Dictionary<int, string>
        {
            [2] = "home",
            [3] = "away",
            [4] = "league",
            [5] = "date",
            [6] = "1x2_h",
            [7] = "1x2_d",
            [8] = "1x2_a",
            [18] = "o_2.5",
            [34] = "u_2.5",
            [49] = "ah_-1_h",
            [50] = "ah_-1_a",
            [53] = "ah_-0.5_h",
            [54] = "ah_-0.5_a",
            [57] = "ah_0_h",
            [58] = "ah_0_a",
            [71] = "ah_+0.5_h",
            [72] = "ah_+0.5_a"
        };

        foreach (var (column, expectedHeader) in requiredHeaders)
        {
            var actualHeader = NormalizeHeader(worksheet.Cells[1, column].Value?.ToString());
            if (!string.Equals(actualHeader, expectedHeader, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected header '{expectedHeader}' at column {column}, but found '{actualHeader ?? "<null>"}'.");
            }
        }
    }

    private static string? NormalizeHeader(string? header)
    {
        return string.IsNullOrWhiteSpace(header)
            ? null
            : header.Replace('\u00A0', ' ').Trim();
    }
}
