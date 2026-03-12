using System;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260312153000_AddCalibrationProfiles")]
    public partial class AddCalibrationProfiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RawConfidenceScore",
                table: "Predictions",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MarketCalibrationProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    BucketStart = table.Column<double>(type: "double precision", nullable: false),
                    BucketEnd = table.Column<double>(type: "double precision", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    SuccessCount = table.Column<int>(type: "integer", nullable: false),
                    CalibratedProbability = table.Column<double>(type: "double precision", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketCalibrationProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketCalibrationProfiles_Market_BucketStart",
                table: "MarketCalibrationProfiles",
                columns: new[] { "Market", "BucketStart" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketCalibrationProfiles");

            migrationBuilder.DropColumn(
                name: "RawConfidenceScore",
                table: "Predictions");
        }
    }
}
