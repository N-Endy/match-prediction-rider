using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExtendRetentionTeamMatchStatsAndUniqueScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MatchScores_MatchLocalDate_HomeTeamKey_AwayTeamKey_LeagueKey",
                table: "MatchScores");

            // Safe on empty DBs; required before the unique finished-fixture index when duplicates exist.
            migrationBuilder.Sql("""
                DELETE FROM "MatchScores" AS ms
                USING (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "MatchLocalDate", "HomeTeamKey", "AwayTeamKey", "LeagueKey"
                               ORDER BY "Id" DESC
                           ) AS rn
                    FROM "MatchScores"
                    WHERE "IsLive" = false
                      AND "HomeTeamKey" <> ''
                      AND "AwayTeamKey" <> ''
                      AND "LeagueKey" <> ''
                ) AS d
                WHERE ms."Id" = d."Id"
                  AND d.rn > 1;
                """);

            migrationBuilder.CreateTable(
                name: "TeamMatchStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeamId = table.Column<int>(type: "integer", nullable: true),
                    OpponentTeamId = table.Column<int>(type: "integer", nullable: true),
                    KickoffUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MatchLocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LeagueKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FixtureKey = table.Column<string>(type: "text", nullable: true),
                    IsHome = table.Column<bool>(type: "boolean", nullable: false),
                    TeamName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OpponentName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    League = table.Column<string>(type: "text", nullable: false),
                    GoalsFor = table.Column<short>(type: "smallint", nullable: true),
                    GoalsAgainst = table.Column<short>(type: "smallint", nullable: true),
                    ExpectedGoalsFor = table.Column<float>(type: "real", nullable: true),
                    ExpectedGoalsAgainst = table.Column<float>(type: "real", nullable: true),
                    Shots = table.Column<short>(type: "smallint", nullable: true),
                    ShotsOnTarget = table.Column<short>(type: "smallint", nullable: true),
                    SourceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceMatchId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRetroactive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMatchStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchScores_FinishedFixture_Unique",
                table: "MatchScores",
                columns: new[] { "MatchLocalDate", "HomeTeamKey", "AwayTeamKey", "LeagueKey" },
                unique: true,
                filter: "\"IsLive\" = false AND \"HomeTeamKey\" <> '' AND \"AwayTeamKey\" <> '' AND \"LeagueKey\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMatchStats_KickoffUtc_AvailableFromUtc",
                table: "TeamMatchStats",
                columns: new[] { "KickoffUtc", "AvailableFromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamMatchStats_SourceName_SourceMatchId_IsHome",
                table: "TeamMatchStats",
                columns: new[] { "SourceName", "SourceMatchId", "IsHome" },
                unique: true,
                filter: "\"SourceMatchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMatchStats_TeamName_KickoffUtc",
                table: "TeamMatchStats",
                columns: new[] { "TeamName", "KickoffUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeamMatchStats");

            migrationBuilder.DropIndex(
                name: "IX_MatchScores_FinishedFixture_Unique",
                table: "MatchScores");

            migrationBuilder.CreateIndex(
                name: "IX_MatchScores_MatchLocalDate_HomeTeamKey_AwayTeamKey_LeagueKey",
                table: "MatchScores",
                columns: new[] { "MatchLocalDate", "HomeTeamKey", "AwayTeamKey", "LeagueKey" });
        }
    }
}
