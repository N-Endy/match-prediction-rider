using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;
using System.Globalization;

namespace MatchPredictor.Infrastructure;

public class ExtractFromExcel : IExtractFromExcel
{
    private const string DefaultWorksheetName = "tennis";
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExtractFromExcel> _logger;

    public ExtractFromExcel(IConfiguration configuration, ILogger<ExtractFromExcel> logger)
    {
        _configuration = configuration;
        _logger = logger;
        EpplusLicenseBootstrapper.EnsureInitialized(configuration, logger);
    }

    public IEnumerable<MatchData> ExtractMatchDatasetFromFile(DateTime? targetLocalDate = null)
    {
        var extractedData = new List<MatchData>();
        var filePath = GetFilePath();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Excel file not found at path: " + filePath, filePath);
        }

        using var package = new ExcelPackage(new FileInfo(filePath));
        if (package.Workbook.Worksheets.Count == 0)
        {
            return extractedData;
        }

        var worksheet = ResolveWorksheet(package.Workbook.Worksheets);
        if (worksheet.Dimension == null || worksheet.Dimension.Rows < 2)
        {
            return extractedData;
        }

        ValidateExpectedHeaders(worksheet);
        var targetDate = (targetLocalDate ?? DateTimeProvider.GetLocalTime()).Date;

        for (var row = 2; row <= worksheet.Dimension.Rows; row++)
        {
            var parsedKickoff = ParseWorksheetDate(worksheet.Cells[row, 5].Value?.ToString());
            if (!parsedKickoff.HasValue || parsedKickoff.Value.Date != targetDate)
            {
                continue;
            }

            var handicap = ResolveSetHandicap(worksheet, row);
            var localDate = DateOnly.FromDateTime(parsedKickoff.Value);
            var localTime = TimeOnly.FromDateTime(parsedKickoff.Value);
            var utcKickoff = DateTimeProvider.ConvertLocalToUtc(parsedKickoff.Value);
            var tournament = worksheet.Cells[row, 4].Value?.ToString()?.Trim();

            var match = new MatchData
            {
                SourceMatchId = worksheet.Cells[row, 1].Value?.ToString()?.Trim(),
                Date = DateTimeProvider.FormatLocalDate(localDate),
                Time = DateTimeProvider.FormatLocalTime(localTime),
                MatchLocalDate = localDate,
                MatchLocalTime = localTime,
                MatchDateTime = utcKickoff,
                Tournament = tournament,
                League = tournament,
                HomeTeam = worksheet.Cells[row, 2].Value?.ToString()?.Trim(),
                AwayTeam = worksheet.Cells[row, 3].Value?.ToString()?.Trim(),
                HomeWin = ParseProbability(worksheet.Cells[row, 6].Value),
                AwayWin = ParseProbability(worksheet.Cells[row, 7].Value),
                OverTwoPointFiveSets = ParseProbability(worksheet.Cells[row, 8].Value),
                UnderTwoPointFiveSets = ParseProbability(worksheet.Cells[row, 9].Value),
                SetHandicapHome = handicap.homeProbability,
                SetHandicapAway = handicap.awayProbability,
                SetHandicapLine = handicap.homeLine,
                SetHandicapLabel = handicap.label
            };

            match.NormalizeSourceProbabilities();
            extractedData.Add(match);
        }

        return extractedData;
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
            Path.Combine("/app/Resources", fileName)
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                _logger.LogInformation("Using workbook at {Path}", path);
                return path;
            }
        }

        return Path.Combine(baseDirFolder, fileName);
    }

    private ExcelWorksheet ResolveWorksheet(ExcelWorksheets worksheets)
    {
        var configuredName = (_configuration["ScrapingValues:TennisWorksheetName"] ?? DefaultWorksheetName).Trim();
        var byName = worksheets.FirstOrDefault(sheet => string.Equals(sheet.Name, configuredName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        foreach (var sheet in worksheets)
        {
            if (LooksLikeTennisWorksheet(sheet))
            {
                return sheet;
            }
        }

        throw new InvalidOperationException($"Could not find the tennis worksheet '{configuredName}' or any sheet matching the tennis header signature.");
    }

    private static bool LooksLikeTennisWorksheet(ExcelWorksheet worksheet)
    {
        if (worksheet.Dimension == null || worksheet.Dimension.Rows < 2)
        {
            return false;
        }

        return NormalizeHeader(worksheet.Cells[1, 2].Value?.ToString()) == "home" &&
               NormalizeHeader(worksheet.Cells[1, 3].Value?.ToString()) == "away" &&
               NormalizeHeader(worksheet.Cells[1, 6].Value?.ToString()) == "1x2_h" &&
               NormalizeHeader(worksheet.Cells[1, 7].Value?.ToString()) == "1x2_a" &&
               NormalizeHeader(worksheet.Cells[1, 8].Value?.ToString()) == "o_2.5" &&
               NormalizeHeader(worksheet.Cells[1, 9].Value?.ToString()) == "u_2.5";
    }

    private static void ValidateExpectedHeaders(ExcelWorksheet worksheet)
    {
        var requiredHeaders = new Dictionary<int, string>
        {
            [1] = "id",
            [2] = "home",
            [3] = "away",
            [4] = "league",
            [5] = "date",
            [6] = "1x2_h",
            [7] = "1x2_a",
            [8] = "o_2.5",
            [9] = "u_2.5",
            [10] = "ah_-1.5_h",
            [11] = "ah_-1.5_a",
            [12] = "ah_+1.5_h",
            [13] = "ah_+1.5_a"
        };

        foreach (var (column, expectedHeader) in requiredHeaders)
        {
            var actualHeader = NormalizeHeader(worksheet.Cells[1, column].Value?.ToString());
            if (!string.Equals(actualHeader, expectedHeader, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Expected header '{expectedHeader}' at column {column}, but found '{actualHeader ?? "<null>"}'.");
            }
        }
    }

    private static (double homeProbability, double awayProbability, double homeLine, string label) ResolveSetHandicap(ExcelWorksheet worksheet, int row)
    {
        var minusHome = ParseProbability(worksheet.Cells[row, 10].Value);
        var minusAway = ParseProbability(worksheet.Cells[row, 11].Value);
        if (minusHome > 0 && minusAway > 0)
        {
            return (minusHome, minusAway, -1.5, "home -1.5 / away +1.5");
        }

        var plusHome = ParseProbability(worksheet.Cells[row, 12].Value);
        var plusAway = ParseProbability(worksheet.Cells[row, 13].Value);
        if (plusHome > 0 && plusAway > 0)
        {
            return (plusHome, plusAway, 1.5, "home +1.5 / away -1.5");
        }

        return (0.0, 0.0, -1.5, string.Empty);
    }

    private static DateTime? ParseWorksheetDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string[] formats = ["d.M.yyyy H:mm", "d.M.yyyy HH:mm", "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm"];
        return DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static double ParseProbability(object? value)
    {
        return double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
    }

    private static string? NormalizeHeader(string? header)
    {
        return string.IsNullOrWhiteSpace(header)
            ? null
            : header.Replace('\u00A0', ' ').Trim();
    }
}
