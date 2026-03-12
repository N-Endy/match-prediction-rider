using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddThresholdProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ThresholdProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    HitRate = table.Column<double>(type: "double precision", nullable: false),
                    PublishedPerWeek = table.Column<double>(type: "double precision", nullable: false),
                    AverageCalibratedProbability = table.Column<double>(type: "double precision", nullable: false),
                    ObservedFrequency = table.Column<double>(type: "double precision", nullable: false),
                    BrierScore = table.Column<double>(type: "double precision", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThresholdProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ThresholdProfiles_Market",
                table: "ThresholdProfiles",
                column: "Market",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ThresholdProfiles");
        }
    }
}
