using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBetaCalibrationProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BetaCalibrationProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    Beta = table.Column<double>(type: "double precision", nullable: false),
                    Gamma = table.Column<double>(type: "double precision", nullable: false),
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
                    table.PrimaryKey("PK_BetaCalibrationProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BetaCalibrationProfiles_Market",
                table: "BetaCalibrationProfiles",
                column: "Market",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BetaCalibrationProfiles");
        }
    }
}
