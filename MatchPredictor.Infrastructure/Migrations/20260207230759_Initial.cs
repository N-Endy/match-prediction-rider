using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    Xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MatchDatas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<string>(type: "TEXT", nullable: true),
                    Time = table.Column<string>(type: "TEXT", nullable: true),
                    League = table.Column<string>(type: "TEXT", nullable: true),
                    HomeTeam = table.Column<string>(type: "TEXT", nullable: true),
                    AwayTeam = table.Column<string>(type: "TEXT", nullable: true),
                    HomeWin = table.Column<double>(type: "REAL", nullable: false),
                    Draw = table.Column<double>(type: "REAL", nullable: false),
                    AwayWin = table.Column<double>(type: "REAL", nullable: false),
                    OverTwoGoals = table.Column<double>(type: "REAL", nullable: false),
                    OverThreeGoals = table.Column<double>(type: "REAL", nullable: false),
                    UnderTwoGoals = table.Column<double>(type: "REAL", nullable: false),
                    UnderThreeGoals = table.Column<double>(type: "REAL", nullable: false),
                    OverFourGoals = table.Column<double>(type: "REAL", nullable: false),
                    Score = table.Column<string>(type: "TEXT", nullable: true),
                    BttsLabel = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchDatas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MatchScores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    League = table.Column<string>(type: "TEXT", nullable: false),
                    HomeTeam = table.Column<string>(type: "TEXT", nullable: false),
                    AwayTeam = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<string>(type: "TEXT", nullable: false),
                    MatchTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BTTSLabel = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchScores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Predictions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    Time = table.Column<string>(type: "TEXT", nullable: false),
                    League = table.Column<string>(type: "TEXT", nullable: false),
                    HomeTeam = table.Column<string>(type: "TEXT", nullable: false),
                    AwayTeam = table.Column<string>(type: "TEXT", nullable: false),
                    PredictionCategory = table.Column<string>(type: "TEXT", nullable: false),
                    PredictedOutcome = table.Column<string>(type: "TEXT", nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "TEXT", nullable: true),
                    ActualOutcome = table.Column<string>(type: "TEXT", nullable: true),
                    ActualScore = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Predictions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScrapingLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapingLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "MatchDatas");

            migrationBuilder.DropTable(
                name: "MatchScores");

            migrationBuilder.DropTable(
                name: "Predictions");

            migrationBuilder.DropTable(
                name: "ScrapingLogs");
        }
    }
}
