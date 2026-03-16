using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase1SecurityAndHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventName",
                table: "ScrapingLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "general");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingLogs_EventName_Timestamp",
                table: "ScrapingLogs",
                columns: new[] { "EventName", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingLogs_Timestamp",
                table: "ScrapingLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_Date_HomeTeam_AwayTeam_League",
                table: "Predictions",
                columns: new[] { "Date", "HomeTeam", "AwayTeam", "League" });

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_Date_IsLive",
                table: "Predictions",
                columns: new[] { "Date", "IsLive" });

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_Date_PredictionCategory_Time",
                table: "Predictions",
                columns: new[] { "Date", "PredictionCategory", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchScores_MatchTime_HomeTeam_AwayTeam",
                table: "MatchScores",
                columns: new[] { "MatchTime", "HomeTeam", "AwayTeam" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchScores_MatchTime_IsLive",
                table: "MatchScores",
                columns: new[] { "MatchTime", "IsLive" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_Date_HomeTeam_AwayTeam",
                table: "MatchDatas",
                columns: new[] { "Date", "HomeTeam", "AwayTeam" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_Date_League",
                table: "MatchDatas",
                columns: new[] { "Date", "League" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_Date_Time",
                table: "MatchDatas",
                columns: new[] { "Date", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_AiScoreMatchScores_MatchTime_HomeTeam_AwayTeam",
                table: "AiScoreMatchScores",
                columns: new[] { "MatchTime", "HomeTeam", "AwayTeam" });

            migrationBuilder.CreateIndex(
                name: "IX_AiScoreMatchScores_MatchTime_IsLive",
                table: "AiScoreMatchScores",
                columns: new[] { "MatchTime", "IsLive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScrapingLogs_EventName_Timestamp",
                table: "ScrapingLogs");

            migrationBuilder.DropIndex(
                name: "IX_ScrapingLogs_Timestamp",
                table: "ScrapingLogs");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_Date_HomeTeam_AwayTeam_League",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_Date_IsLive",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_Date_PredictionCategory_Time",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_MatchScores_MatchTime_HomeTeam_AwayTeam",
                table: "MatchScores");

            migrationBuilder.DropIndex(
                name: "IX_MatchScores_MatchTime_IsLive",
                table: "MatchScores");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_Date_HomeTeam_AwayTeam",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_Date_League",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_Date_Time",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_AiScoreMatchScores_MatchTime_HomeTeam_AwayTeam",
                table: "AiScoreMatchScores");

            migrationBuilder.DropIndex(
                name: "IX_AiScoreMatchScores_MatchTime_IsLive",
                table: "AiScoreMatchScores");

            migrationBuilder.DropColumn(
                name: "EventName",
                table: "ScrapingLogs");
        }
    }
}
