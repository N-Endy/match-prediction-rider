using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using OfficeOpenXml;

namespace MatchPredictor.Infrastructure;

public class ExtractFromExcel : IExtractFromExcel
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExtractFromExcel> _logger;

    public ExtractFromExcel(IConfiguration configuration, ILogger<ExtractFromExcel> logger)
    {
        _configuration = configuration;
        _logger = logger;
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
            if (File.Exists(path))
            {
                return path;
            }
        }

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
        //ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        ExcelPackage.License.SetNonCommercialPersonal("My Name"); //This will also set the Author property to the name provided in the argument.
        using(var _ = new ExcelPackage(new FileInfo("MyWorkbook.xlsx")))
        {

        }
        
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

            if (package.Workbook.Worksheets.Count > 0)
            {
                var worksheet = package.Workbook.Worksheets[0];

                if (worksheet.Dimension == null || worksheet.Cells.Any(cell => cell.Value == null))
                {
                    _logger.LogWarning("❌ Excel file is empty.");
                    return extractedData;
                }

                var rowCount = worksheet.Dimension?.Rows ?? 0;

                for (var row = 2; row <= rowCount; row++)
                {
                    var dateString = worksheet.Cells[row, 5].Value?.ToString();
                    if (dateString != null)
                    {
                        var datePart = dateString.Split(' ')[0].Split('.');
                        if (datePart.Length > 0 && int.TryParse(datePart[0], out int day))
                        {
                            var currentDay = DateTime.Now.Day;

                            if (day == currentDay)
                            {
                                var dt = DateTime.ParseExact(dateString, "d.M.yyyy H:mm", null);
                                dateString = dt.ToString("dd-MM-yyyy, HH:mm");
                                var matchData = new MatchData
                                {
                                    Date = DateTime.ParseExact(dateString.Split(',')[0], "dd-MM-yyyy", null).ToString("dd-MM-yyyy"),
                                    Time = dateString.Split(',')[1].Trim(),
                                    League = worksheet.Cells[row, 4].Value?.ToString(),
                                    HomeTeam = worksheet.Cells[row, 2].Value?.ToString(),
                                    AwayTeam = worksheet.Cells[row, 3].Value?.ToString(),
                                    HomeWin = double.TryParse(worksheet.Cells[row, 6].Value?.ToString(), out var homeWin) ? homeWin : 0,
                                    Draw = double.TryParse(worksheet.Cells[row, 7].Value?.ToString(), out var draw) ? draw : 0,
                                    AwayWin = double.TryParse(worksheet.Cells[row, 8].Value?.ToString(), out var awayWin) ? awayWin : 0,
                                    
                                    // Granular Over/Under lines
                                    OverOneGoal = double.TryParse(worksheet.Cells[row, 12].Value?.ToString(), out var o1) ? o1 : 0,
                                    OverOnePointFive = double.TryParse(worksheet.Cells[row, 14].Value?.ToString(), out var o15) ? o15 : 0,
                                    OverTwoGoals = double.TryParse(worksheet.Cells[row, 18].Value?.ToString(), out var overTwoGoals) ? overTwoGoals : 0,
                                    OverThreeGoals = double.TryParse(worksheet.Cells[row, 22].Value?.ToString(), out var overThreeGoals) ? overThreeGoals : 0,
                                    OverFourGoals = double.TryParse(worksheet.Cells[row, 24].Value?.ToString(), out var overFourGoals) ? overFourGoals : 0,
                                    UnderOnePointFive = double.TryParse(worksheet.Cells[row, 30].Value?.ToString(), out var u15) ? u15 : 0,
                                    UnderTwoGoals = double.TryParse(worksheet.Cells[row, 34].Value?.ToString(), out var underTwoGoals) ? underTwoGoals : 0,
                                    UnderThreeGoals = double.TryParse(worksheet.Cells[row, 38].Value?.ToString(), out var underThreeGoals) ? underThreeGoals : 0,
                                    
                                    // Asian Handicap data
                                    AhZeroHome = double.TryParse(worksheet.Cells[row, 57].Value?.ToString(), out var ah0h) ? ah0h : 0,
                                    AhZeroAway = double.TryParse(worksheet.Cells[row, 58].Value?.ToString(), out var ah0a) ? ah0a : 0,
                                    AhMinusHalfHome = double.TryParse(worksheet.Cells[row, 53].Value?.ToString(), out var ahm05h) ? ahm05h : 0,
                                    AhMinusHalfAway = double.TryParse(worksheet.Cells[row, 54].Value?.ToString(), out var ahm05a) ? ahm05a : 0,
                                    AhMinusOneHome = double.TryParse(worksheet.Cells[row, 49].Value?.ToString(), out var ahm1h) ? ahm1h : 0,
                                    AhMinusOneAway = double.TryParse(worksheet.Cells[row, 50].Value?.ToString(), out var ahm1a) ? ahm1a : 0,
                                    AhPlusHalfHome = double.TryParse(worksheet.Cells[row, 71].Value?.ToString(), out var ahp05h) ? ahp05h : 0,
                                    AhPlusHalfAway = double.TryParse(worksheet.Cells[row, 72].Value?.ToString(), out var ahp05a) ? ahp05a : 0,
                                };
                                extractedData.Add(matchData);
                            }
                        }
                    }
                }
            }
            else
            {
                _logger.LogWarning("❌ Excel file does not contain any worksheets.");
                return extractedData;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "❌ An error occurred while extracting data from Excel file.");
            throw; // Re-throw the exception to handle it further up if needed
        }
        return extractedData;
    }
}