using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlementSourceIdsAndAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EventId",
                table: "SofaScoreMatchScores",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettledSourceEventId",
                table: "Predictions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettledSourceName",
                table: "Predictions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettledSourceEventId",
                table: "ForecastObservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettledSourceName",
                table: "ForecastObservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AwayTeamId",
                table: "AiScoreMatchScores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HomeTeamId",
                table: "AiScoreMatchScores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceEventId",
                table: "AiScoreMatchScores",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SofaScoreMatchScores_EventId",
                table: "SofaScoreMatchScores",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_SettledSourceName_SettledSourceEventId",
                table: "Predictions",
                columns: new[] { "SettledSourceName", "SettledSourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastObservations_SettledSourceName_SettledSourceEventId",
                table: "ForecastObservations",
                columns: new[] { "SettledSourceName", "SettledSourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiScoreMatchScores_SourceEventId",
                table: "AiScoreMatchScores",
                column: "SourceEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SofaScoreMatchScores_EventId",
                table: "SofaScoreMatchScores");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_SettledSourceName_SettledSourceEventId",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_ForecastObservations_SettledSourceName_SettledSourceEventId",
                table: "ForecastObservations");

            migrationBuilder.DropIndex(
                name: "IX_AiScoreMatchScores_SourceEventId",
                table: "AiScoreMatchScores");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "SofaScoreMatchScores");

            migrationBuilder.DropColumn(
                name: "SettledSourceEventId",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "SettledSourceName",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "SettledSourceEventId",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "SettledSourceName",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "AwayTeamId",
                table: "AiScoreMatchScores");

            migrationBuilder.DropColumn(
                name: "HomeTeamId",
                table: "AiScoreMatchScores");

            migrationBuilder.DropColumn(
                name: "SourceEventId",
                table: "AiScoreMatchScores");
        }
    }
}
