using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    public partial class Phase4ModelQuality : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceQualityProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceName = table.Column<string>(type: "text", nullable: false),
                    LeagueKey = table.Column<string>(type: "text", nullable: false),
                    LeagueLabel = table.Column<string>(type: "text", nullable: false),
                    TimeBucketKey = table.Column<string>(type: "text", nullable: false),
                    TimeBucketLabel = table.Column<string>(type: "text", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    FinishedCoverageCount = table.Column<int>(type: "integer", nullable: false),
                    ExactScoreMatchCount = table.Column<int>(type: "integer", nullable: false),
                    LiveOnlyCount = table.Column<int>(type: "integer", nullable: false),
                    AverageKickoffOffsetMinutes = table.Column<double>(type: "double precision", nullable: false),
                    ReliabilityScore = table.Column<double>(type: "double precision", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceQualityProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceQualityProfiles_SourceName_LeagueKey_TimeBucketKey",
                table: "SourceQualityProfiles",
                columns: new[] { "SourceName", "LeagueKey", "TimeBucketKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceQualityProfiles_SourceName_ReliabilityScore",
                table: "SourceQualityProfiles",
                columns: new[] { "SourceName", "ReliabilityScore" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceQualityProfiles");
        }
    }
}
