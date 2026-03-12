using MatchPredictor.Infrastructure;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ExtractFromExcelTests
{
    [Fact]
    public void ExtractMatchDatasetFromFile_ReadsSoccerSheet_CurrentDayOnly_AndNormalizesSourceProbabilities()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"matchpredictor-excel-{Guid.NewGuid():N}");
        var resourcesDir = Path.Combine(tempRoot, "Resources");
        Directory.CreateDirectory(resourcesDir);

        var originalCurrentDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(tempRoot);
            var workbookPath = Path.Combine(resourcesDir, "test-predictions.xlsx");
            CreateWorkbook(workbookPath);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ScrapingValues:PredictionsFileName"] = "test-predictions.xlsx",
                    ["EPPlus:ExcelPackage:License"] = "NonCommercialPersonal:Tests"
                })
                .Build();

            var extractor = new ExtractFromExcel(configuration, NullLogger<ExtractFromExcel>.Instance);

            var matches = extractor.ExtractMatchDatasetFromFile().ToList();

            var match = Assert.Single(matches);
            Assert.Equal("Home FC", match.HomeTeam);
            Assert.Equal("Away FC", match.AwayTeam);
            Assert.Equal("Premier Test League", match.League);
            Assert.Equal(1.0, match.HomeWin + match.Draw + match.AwayWin, 6);
            Assert.Equal(1.0, match.OverTwoGoals + match.UnderTwoGoals, 6);
            Assert.Equal(DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy"), match.Date);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void CreateWorkbook(string workbookPath)
    {
        ExcelPackage.License.SetNonCommercialPersonal("Tests");

        using var package = new ExcelPackage();
        var worksheet = package.Workbook.Worksheets.Add("soccer");

        var headers = new Dictionary<int, string>
        {
            [2] = "home",
            [3] = "away",
            [4] = "league",
            [5] = "date",
            [6] = "1x2_h",
            [7] = "1x2_d",
            [8] = "1x2_a",
            [12] = "o_1",
            [14] = "o_1.5",
            [18] = "o_2.5",
            [22] = "o_3.5",
            [24] = "o_4",
            [30] = "u_1.5",
            [34] = "u_2.5",
            [38] = "u_3.5",
            [49] = "ah_-1_h",
            [50] = "ah_-1_a",
            [53] = "ah_-0.5_h",
            [54] = "ah_-0.5_a",
            [57] = "ah_0_h",
            [58] = "ah_0_a",
            [71] = "ah_+0.5_h",
            [72] = "ah_+0.5_a"
        };

        foreach (var (column, header) in headers)
            worksheet.Cells[1, column].Value = header;

        var today = DateTimeProvider.GetLocalTime().Date;
        var tomorrow = today.AddDays(1);

        WriteRow(worksheet, 2, today, "Home FC", "Away FC", 0.50, 0.25, 0.20, 0.41, 0.49);
        WriteRow(worksheet, 3, tomorrow, "Tomorrow FC", "Later FC", 0.55, 0.20, 0.25, 0.60, 0.40);

        package.SaveAs(new FileInfo(workbookPath));
    }

    private static void WriteRow(
        ExcelWorksheet worksheet,
        int row,
        DateTime matchDate,
        string homeTeam,
        string awayTeam,
        double homeWin,
        double draw,
        double awayWin,
        double over25,
        double under25)
    {
        worksheet.Cells[row, 2].Value = homeTeam;
        worksheet.Cells[row, 3].Value = awayTeam;
        worksheet.Cells[row, 4].Value = "Premier Test League";
        worksheet.Cells[row, 5].Value = matchDate.ToString("d.M.yyyy H:mm");
        worksheet.Cells[row, 6].Value = homeWin;
        worksheet.Cells[row, 7].Value = draw;
        worksheet.Cells[row, 8].Value = awayWin;
        worksheet.Cells[row, 12].Value = 0.70;
        worksheet.Cells[row, 14].Value = 0.62;
        worksheet.Cells[row, 18].Value = over25;
        worksheet.Cells[row, 22].Value = 0.18;
        worksheet.Cells[row, 24].Value = 0.09;
        worksheet.Cells[row, 30].Value = 0.38;
        worksheet.Cells[row, 34].Value = under25;
        worksheet.Cells[row, 38].Value = 0.82;
        worksheet.Cells[row, 49].Value = 0.45;
        worksheet.Cells[row, 50].Value = 0.55;
        worksheet.Cells[row, 53].Value = 0.51;
        worksheet.Cells[row, 54].Value = 0.49;
        worksheet.Cells[row, 57].Value = 0.48;
        worksheet.Cells[row, 58].Value = 0.52;
        worksheet.Cells[row, 71].Value = 0.59;
        worksheet.Cells[row, 72].Value = 0.41;
    }
}
