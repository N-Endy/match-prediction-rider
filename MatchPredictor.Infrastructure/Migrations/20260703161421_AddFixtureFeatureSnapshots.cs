using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFixtureFeatureSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FixtureFeatureSnapshots",
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
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceName = table.Column<string>(type: "text", nullable: false),
                    HomeRestDays = table.Column<double>(type: "double precision", nullable: true),
                    AwayRestDays = table.Column<double>(type: "double precision", nullable: true),
                    HomeFormPointsPerMatch = table.Column<double>(type: "double precision", nullable: true),
                    AwayFormPointsPerMatch = table.Column<double>(type: "double precision", nullable: true),
                    HomeFormGoalsForPerMatch = table.Column<double>(type: "double precision", nullable: true),
                    AwayFormGoalsForPerMatch = table.Column<double>(type: "double precision", nullable: true),
                    HomeFormGoalsAgainstPerMatch = table.Column<double>(type: "double precision", nullable: true),
                    AwayFormGoalsAgainstPerMatch = table.Column<double>(type: "double precision", nullable: true),
                    HeadToHeadHomeWins = table.Column<int>(type: "integer", nullable: false),
                    HeadToHeadDraws = table.Column<int>(type: "integer", nullable: false),
                    HeadToHeadAwayWins = table.Column<int>(type: "integer", nullable: false),
                    HomeExpectedGoalsFor = table.Column<double>(type: "double precision", nullable: true),
                    AwayExpectedGoalsFor = table.Column<double>(type: "double precision", nullable: true),
                    RawPayloadJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixtureFeatureSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FixtureFeatureSnapshots_FixtureKey_CapturedAtUtc",
                table: "FixtureFeatureSnapshots",
                columns: new[] { "FixtureKey", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FixtureFeatureSnapshots_MatchLocalDate_FixtureKey",
                table: "FixtureFeatureSnapshots",
                columns: new[] { "MatchLocalDate", "FixtureKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FixtureFeatureSnapshots");
        }
    }
}
