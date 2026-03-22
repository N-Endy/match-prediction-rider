using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeTelemetryAndSofaScorePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "ScrapingLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PredictionRunId",
                table: "ScrapingLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunKind",
                table: "ScrapingLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunLabel",
                table: "ScrapingLogs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceName",
                table: "ScrapingLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "ScrapingLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AwaySetsWon",
                table: "MatchScores",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HomeSetsWon",
                table: "MatchScores",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedScoreline",
                table: "MatchScores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AwaySetsWon",
                table: "MatchDatas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HomeSetsWon",
                table: "MatchDatas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedScoreline",
                table: "MatchDatas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetHandicapLabel",
                table: "MatchDatas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceMatchId",
                table: "MatchDatas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Surface",
                table: "MatchDatas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tournament",
                table: "MatchDatas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AwaySetsWon",
                table: "AiScoreMatchScores",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HomeSetsWon",
                table: "AiScoreMatchScores",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedScoreline",
                table: "AiScoreMatchScores",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SofaScoreMatchScores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    League = table.Column<string>(type: "text", nullable: false),
                    HomeTeam = table.Column<string>(type: "text", nullable: false),
                    AwayTeam = table.Column<string>(type: "text", nullable: false),
                    Score = table.Column<string>(type: "text", nullable: false),
                    NormalizedScoreline = table.Column<string>(type: "text", nullable: true),
                    HomeSetsWon = table.Column<int>(type: "integer", nullable: true),
                    AwaySetsWon = table.Column<int>(type: "integer", nullable: true),
                    DisplayedScore = table.Column<string>(type: "text", nullable: true),
                    RegularTimeScore = table.Column<string>(type: "text", nullable: true),
                    HalfTimeScore = table.Column<string>(type: "text", nullable: true),
                    ExtraTimeScore = table.Column<string>(type: "text", nullable: true),
                    StatusText = table.Column<string>(type: "text", nullable: true),
                    EventUrl = table.Column<string>(type: "text", nullable: false),
                    MatchTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsLive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SofaScoreMatchScores", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingLogs_PredictionRunId",
                table: "ScrapingLogs",
                column: "PredictionRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingLogs_RunKind_RunLabel_Timestamp",
                table: "ScrapingLogs",
                columns: new[] { "RunKind", "RunLabel", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingLogs_SourceName_Stage_Timestamp",
                table: "ScrapingLogs",
                columns: new[] { "SourceName", "Stage", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SofaScoreMatchScores_EventUrl",
                table: "SofaScoreMatchScores",
                column: "EventUrl");

            migrationBuilder.CreateIndex(
                name: "IX_SofaScoreMatchScores_MatchTime_HomeTeam_AwayTeam",
                table: "SofaScoreMatchScores",
                columns: new[] { "MatchTime", "HomeTeam", "AwayTeam" });

            migrationBuilder.CreateIndex(
                name: "IX_SofaScoreMatchScores_MatchTime_IsLive",
                table: "SofaScoreMatchScores",
                columns: new[] { "MatchTime", "IsLive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SofaScoreMatchScores");

            migrationBuilder.DropIndex(
                name: "IX_ScrapingLogs_PredictionRunId",
                table: "ScrapingLogs");

            migrationBuilder.DropIndex(
                name: "IX_ScrapingLogs_RunKind_RunLabel_Timestamp",
                table: "ScrapingLogs");

            migrationBuilder.DropIndex(
                name: "IX_ScrapingLogs_SourceName_Stage_Timestamp",
                table: "ScrapingLogs");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "ScrapingLogs");

            migrationBuilder.DropColumn(
                name: "PredictionRunId",
                table: "ScrapingLogs");

            migrationBuilder.DropColumn(
                name: "RunKind",
                table: "ScrapingLogs");

            migrationBuilder.DropColumn(
                name: "RunLabel",
                table: "ScrapingLogs");

            migrationBuilder.DropColumn(
                name: "SourceName",
                table: "ScrapingLogs");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "ScrapingLogs");
        }
    }
}
