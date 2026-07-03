using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIsotonicCalibrationAndHistoricalBacktests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistoricalBacktestSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    BrierScore = table.Column<double>(type: "double precision", nullable: false),
                    ExpectedCalibrationError = table.Column<double>(type: "double precision", nullable: false),
                    LogLoss = table.Column<double>(type: "double precision", nullable: false),
                    FlatStakeRoiPercent = table.Column<double>(type: "double precision", nullable: false),
                    AverageClvPercent = table.Column<double>(type: "double precision", nullable: false),
                    LookbackDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalBacktestSummaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IsotonicCalibrationProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    KnotsJson = table.Column<string>(type: "text", nullable: false),
                    TrainingSampleCount = table.Column<int>(type: "integer", nullable: false),
                    ValidationSampleCount = table.Column<int>(type: "integer", nullable: false),
                    BaselineBrierScore = table.Column<double>(type: "double precision", nullable: false),
                    ValidationBrierScore = table.Column<double>(type: "double precision", nullable: false),
                    Improvement = table.Column<double>(type: "double precision", nullable: false),
                    IsRecommended = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IsotonicCalibrationProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalBacktestSummaries_RunAtUtc",
                table: "HistoricalBacktestSummaries",
                column: "RunAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IsotonicCalibrationProfiles_Market",
                table: "IsotonicCalibrationProfiles",
                column: "Market",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricalBacktestSummaries");

            migrationBuilder.DropTable(
                name: "IsotonicCalibrationProfiles");
        }
    }
}
