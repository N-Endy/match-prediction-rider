using System.Reflection;
using MatchPredictor.Infrastructure;
using OfficeOpenXml;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class ExcelHeaderGoldenFileTests
{
    [Fact]
    public void ValidateExpectedHeaders_AcceptsMinimalSportsAiWorkbookHeaderMap()
    {
        ExcelPackage.License.SetNonCommercialPersonal("MatchPredictor Tests");
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

        foreach (var (column, header) in headers)
        {
            worksheet.Cells[1, column].Value = header;
        }

        var validate = typeof(ExtractFromExcel).GetMethod(
            "ValidateExpectedHeaders",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(validate);
        validate!.Invoke(null, new object[] { worksheet });
    }
}
