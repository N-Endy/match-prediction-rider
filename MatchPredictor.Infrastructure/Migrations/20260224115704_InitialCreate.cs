using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<string>(type: "text", nullable: true),
                    Time = table.Column<string>(type: "text", nullable: true),
                    MatchDateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    League = table.Column<string>(type: "text", nullable: true),
                    HomeTeam = table.Column<string>(type: "text", nullable: true),
                    AwayTeam = table.Column<string>(type: "text", nullable: true),
                    HomeWin = table.Column<double>(type: "double precision", nullable: false),
                    Draw = table.Column<double>(type: "double precision", nullable: false),
                    AwayWin = table.Column<double>(type: "double precision", nullable: false),
                    OverTwoGoals = table.Column<double>(type: "double precision", nullable: false),
                    OverThreeGoals = table.Column<double>(type: "double precision", nullable: false),
                    UnderTwoGoals = table.Column<double>(type: "double precision", nullable: false),
                    UnderThreeGoals = table.Column<double>(type: "double precision", nullable: false),
                    OverFourGoals = table.Column<double>(type: "double precision", nullable: false),
                    OverOneGoal = table.Column<double>(type: "double precision", nullable: false),
                    OverOnePointFive = table.Column<double>(type: "double precision", nullable: false),
                    UnderOnePointFive = table.Column<double>(type: "double precision", nullable: false),
                    AhZeroHome = table.Column<double>(type: "double precision", nullable: false),
                    AhZeroAway = table.Column<double>(type: "double precision", nullable: false),
                    AhMinusHalfHome = table.Column<double>(type: "double precision", nullable: false),
                    AhMinusHalfAway = table.Column<double>(type: "double precision", nullable: false),
                    AhMinusOneHome = table.Column<double>(type: "double precision", nullable: false),
                    AhMinusOneAway = table.Column<double>(type: "double precision", nullable: false),
                    AhPlusHalfHome = table.Column<double>(type: "double precision", nullable: false),
                    AhPlusHalfAway = table.Column<double>(type: "double precision", nullable: false),
                    Score = table.Column<string>(type: "text", nullable: true),
                    BttsLabel = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchDatas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MatchScores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    League = table.Column<string>(type: "text", nullable: false),
                    HomeTeam = table.Column<string>(type: "text", nullable: false),
                    AwayTeam = table.Column<string>(type: "text", nullable: false),
                    Score = table.Column<string>(type: "text", nullable: false),
                    MatchTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BTTSLabel = table.Column<bool>(type: "boolean", nullable: false),
                    IsLive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchScores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Predictions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<string>(type: "text", nullable: false),
                    Time = table.Column<string>(type: "text", nullable: false),
                    MatchDateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    League = table.Column<string>(type: "text", nullable: false),
                    HomeTeam = table.Column<string>(type: "text", nullable: false),
                    AwayTeam = table.Column<string>(type: "text", nullable: false),
                    PredictionCategory = table.Column<string>(type: "text", nullable: false),
                    PredictedOutcome = table.Column<string>(type: "text", nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "numeric", nullable: true),
                    ActualOutcome = table.Column<string>(type: "text", nullable: true),
                    ActualScore = table.Column<string>(type: "text", nullable: true),
                    IsLive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Predictions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegressionPredictions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<string>(type: "text", nullable: false),
                    Time = table.Column<string>(type: "text", nullable: false),
                    League = table.Column<string>(type: "text", nullable: false),
                    HomeTeam = table.Column<string>(type: "text", nullable: false),
                    AwayTeam = table.Column<string>(type: "text", nullable: false),
                    PredictionCategory = table.Column<string>(type: "text", nullable: false),
                    PredictedOutcome = table.Column<string>(type: "text", nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "numeric", nullable: false),
                    ExpectedHomeGoals = table.Column<double>(type: "double precision", nullable: false),
                    ExpectedAwayGoals = table.Column<double>(type: "double precision", nullable: false),
                    ActualOutcome = table.Column<string>(type: "text", nullable: true),
                    ActualScore = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegressionPredictions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScrapingLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true)
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
                name: "RegressionPredictions");

            migrationBuilder.DropTable(
                name: "ScrapingLogs");
        }
    }
}
