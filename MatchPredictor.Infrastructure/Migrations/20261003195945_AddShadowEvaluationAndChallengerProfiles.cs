using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShadowEvaluationAndChallengerProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsShadow",
                table: "MarketMlModelProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "PromotionPValue",
                table: "MarketMlModelProfiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ShadowBrierScore",
                table: "MarketMlModelProfiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ShadowLogLoss",
                table: "MarketMlModelProfiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShadowSampleCount",
                table: "MarketMlModelProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MatchLineupSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FixtureKey = table.Column<string>(type: "text", nullable: false),
                    MatchLocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    MatchDateTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    League = table.Column<string>(type: "text", nullable: false),
                    HomeTeam = table.Column<string>(type: "text", nullable: false),
                    AwayTeam = table.Column<string>(type: "text", nullable: false),
                    IsConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    HomeStartingXiJson = table.Column<string>(type: "text", nullable: false),
                    AwayStartingXiJson = table.Column<string>(type: "text", nullable: false),
                    HomeAbsencesJson = table.Column<string>(type: "text", nullable: false),
                    AwayAbsencesJson = table.Column<string>(type: "text", nullable: false),
                    HomeAttackAdjustment = table.Column<double>(type: "double precision", nullable: false),
                    HomeDefenseAdjustment = table.Column<double>(type: "double precision", nullable: false),
                    AwayAttackAdjustment = table.Column<double>(type: "double precision", nullable: false),
                    AwayDefenseAdjustment = table.Column<double>(type: "double precision", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchLineupSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelShadowEvaluations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PredictionId = table.Column<int>(type: "integer", nullable: false),
                    FixtureKey = table.Column<string>(type: "text", nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    PredictedOutcome = table.Column<string>(type: "text", nullable: false),
                    ChampionCalibratedProbability = table.Column<double>(type: "double precision", nullable: false),
                    ChallengerCalibratedProbability = table.Column<double>(type: "double precision", nullable: false),
                    OutcomeOccurred = table.Column<bool>(type: "boolean", nullable: true),
                    ChampionBrierLoss = table.Column<double>(type: "double precision", nullable: true),
                    ChallengerBrierLoss = table.Column<double>(type: "double precision", nullable: true),
                    IsSettled = table.Column<bool>(type: "boolean", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SettledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelShadowEvaluations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchLineupSnapshots_FixtureKey_CapturedAtUtc",
                table: "MatchLineupSnapshots",
                columns: new[] { "FixtureKey", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchLineupSnapshots_MatchLocalDate_IsConfirmed",
                table: "MatchLineupSnapshots",
                columns: new[] { "MatchLocalDate", "IsConfirmed" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelShadowEvaluations_IsSettled_CapturedAtUtc",
                table: "ModelShadowEvaluations",
                columns: new[] { "IsSettled", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelShadowEvaluations_PredictionId_Market",
                table: "ModelShadowEvaluations",
                columns: new[] { "PredictionId", "Market" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchLineupSnapshots");

            migrationBuilder.DropTable(
                name: "ModelShadowEvaluations");

            migrationBuilder.DropColumn(
                name: "IsShadow",
                table: "MarketMlModelProfiles");

            migrationBuilder.DropColumn(
                name: "PromotionPValue",
                table: "MarketMlModelProfiles");

            migrationBuilder.DropColumn(
                name: "ShadowBrierScore",
                table: "MarketMlModelProfiles");

            migrationBuilder.DropColumn(
                name: "ShadowLogLoss",
                table: "MarketMlModelProfiles");

            migrationBuilder.DropColumn(
                name: "ShadowSampleCount",
                table: "MarketMlModelProfiles");
        }
    }
}
