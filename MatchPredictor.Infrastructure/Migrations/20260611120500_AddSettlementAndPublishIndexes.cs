using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260611120500_AddSettlementAndPublishIndexes")]
public partial class AddSettlementAndPublishIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_ForecastObservations_IsSettled_SettledAt",
            table: "ForecastObservations",
            columns: new[] { "IsSettled", "SettledAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Predictions_IsCurrentRevision_WasPublished_MatchDateTime",
            table: "Predictions",
            columns: new[] { "IsCurrentRevision", "WasPublished", "MatchDateTime" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ForecastObservations_IsSettled_SettledAt",
            table: "ForecastObservations");

        migrationBuilder.DropIndex(
            name: "IX_Predictions_IsCurrentRevision_WasPublished_MatchDateTime",
            table: "Predictions");
    }
}
