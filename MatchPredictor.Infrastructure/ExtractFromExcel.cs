using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using OfficeOpenXml;

namespace MatchPredictor.Infrastructure;

/// <summary>
/// Extracts match data from predictions.xlsx file from sports-ai.dev
/// 
/// File Structure:
/// - Multiple worksheets for different sports (football, handball, hockey, volleyball, etc.)
/// - Each row contains: id, home, away, league, date, 1x2_home_prob, 1x2_away_prob
/// - Filters for today's matches (matching current date)
/// </summary>
public class ExtractFromExcel : IExtractFromExcel
{
    private readonly string _filePath;
    
    // Sports to include (football/soccer only for main predictions)
    private static readonly HashSet<string> IncludedSports = new(StringComparer.OrdinalIgnoreCase)
    {
        "soccer"  // Primary sport
    };

    public ExtractFromExcel()
    {
        var projectDirectory = Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? string.Empty;
        _filePath = Path.Combine(projectDirectory, "Resources/predictions.xlsx");
    }
    
    public IEnumerable<MatchData> ExtractMatchDatasetFromFile()
    {
        ExcelPackage.License.SetNonCommercialPersonal("MatchPredictor");
        var extractedData = new List<MatchData>();

        try
        {
            if (!File.Exists(_filePath))
            {
                Console.WriteLine($"Excel file not found at path: {_filePath}");
                throw new FileNotFoundException($"Excel file not found at path: {_filePath}");
            }

            if (new FileInfo(_filePath).Length == 0)
            {
                Console.WriteLine("Excel file is empty.");
                return extractedData;
            }

            using var package = new ExcelPackage(new FileInfo(_filePath));

            if (package.Workbook.Worksheets.Count == 0)
            {
                Console.WriteLine("No worksheets found in the Excel file.");
                return extractedData;
            }

            var currentDay = DateTime.Now.Day;
            var currentMonth = DateTime.Now.Month;
            var currentYear = DateTime.Now.Year;

            // Iterate through all worksheets
            foreach (var worksheet in package.Workbook.Worksheets)
            {
                var sportName = worksheet.Name;

                // Filter to only include football
                if (!IncludedSports.Contains(sportName)) continue;

                Console.WriteLine($"Reading sheet: {sportName}");

                if (worksheet.Dimension == null)
                    continue;

                var rowCount = worksheet.Dimension.Rows;
                if (rowCount < 2)
                    continue;

                // Column mapping for sports-ai.dev format:
                // Column 1: id
                // Column 2: home
                // Column 3: away
                // Column 4: league
                // Column 5: date (format: "d.M.yyyy H:mm")
                // Column 6: 1x2_home (home win probability)
                // Column 7: 1x2_away (away win probability)

                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        var dateString = worksheet.Cells[row, 5].Value?.ToString();
                        if (string.IsNullOrWhiteSpace(dateString))
                            continue;

                        // Parse date: format is "d.M.yyyy H:mm"
                        if (!DateTime.TryParseExact(dateString, "d.M.yyyy H:mm", null, 
                            System.Globalization.DateTimeStyles.None, out var matchDateTime))
                        {
                            continue;
                        }

                        // Filter for today's matches
                        if (matchDateTime.Day != currentDay || 
                            matchDateTime.Month != currentMonth || 
                            matchDateTime.Year != currentYear)
                        {
                            continue;
                        }

                        var homeTeam = worksheet.Cells[row, 2].Value?.ToString()?.Trim();
                        var awayTeam = worksheet.Cells[row, 3].Value?.ToString()?.Trim();
                        var league = worksheet.Cells[row, 4].Value?.ToString()?.Trim();

                        if (string.IsNullOrWhiteSpace(homeTeam) || string.IsNullOrWhiteSpace(awayTeam))
                            continue;

                        // Parse probabilities
                        var homeWinProb = TryParseDouble(worksheet.Cells[row, 6].Value);
                        var awayWinProb = TryParseDouble(worksheet.Cells[row, 7].Value);

                        // For sports with only home/away (like handball, hockey):
                        // We calculate draw probability as 1 - homeWin - awayWin
                        var drawProb = Math.Max(0, 1.0 - homeWinProb - awayWinProb);

                        var matchData = new MatchData
                        {
                            Date = matchDateTime.ToString("dd-MM-yyyy"),
                            Time = matchDateTime.ToString("HH:mm"),
                            League = $"{league} ({sportName})",
                            HomeTeam = homeTeam,
                            AwayTeam = awayTeam,
                            HomeWin = homeWinProb,
                            Draw = drawProb,
                            AwayWin = awayWinProb,
                            // For sports without detailed goal odds, estimate based on win probabilities
                            // Higher ratio suggests more attacking/goals expected
                            OverTwoGoals = (homeWinProb + awayWinProb) * 0.6, // Estimate
                            OverThreeGoals = (homeWinProb + awayWinProb) * 0.4, // Estimate
                            UnderTwoGoals = (1.0 - (homeWinProb + awayWinProb)) * 0.7, // Estimate
                            UnderThreeGoals = (1.0 - (homeWinProb + awayWinProb)) * 0.8, // Estimate
                            OverFourGoals = (homeWinProb + awayWinProb) * 0.25, // Estimate
                        };

                        extractedData.Add(matchData);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing row {row} in sheet {sportName}: {ex.Message}");
                        continue;
                    }
                }
            }

            Console.WriteLine($"Successfully extracted {extractedData.Count} matches from Excel file.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while extracting data from the Excel file: {ex.Message}");
            throw;
        }

        return extractedData;
    }

    /// <summary>
    /// Safely parse a double value from Excel cell
    /// </summary>
    private static double TryParseDouble(object value)
    {
        if (value == null)
            return 0.0;

        if (double.TryParse(value.ToString(), out var result))
            return result;

        return 0.0;
    }
}