using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdatePredictionOddsSnapshotUniqueFilter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PredictionOddsSnapshots_PredictionId_SourceName_SnapshotKind",
                table: "PredictionOddsSnapshots");

            migrationBuilder.CreateIndex(
                name: "IX_PredictionOddsSnapshots_PredictionId_SourceName_SnapshotKind",
                table: "PredictionOddsSnapshots",
                columns: new[] { "PredictionId", "SourceName", "SnapshotKind" },
                unique: true,
                filter: "\"SnapshotKind\" IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PredictionOddsSnapshots_PredictionId_SourceName_SnapshotKind",
                table: "PredictionOddsSnapshots");

            migrationBuilder.CreateIndex(
                name: "IX_PredictionOddsSnapshots_PredictionId_SourceName_SnapshotKind",
                table: "PredictionOddsSnapshots",
                columns: new[] { "PredictionId", "SourceName", "SnapshotKind" },
                unique: true);
        }
    }
}
