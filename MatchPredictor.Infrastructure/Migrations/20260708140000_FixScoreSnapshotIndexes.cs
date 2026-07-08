using System;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260708140000_FixScoreSnapshotIndexes")]
public partial class FixScoreSnapshotIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        AddSnapshotKeyColumns(migrationBuilder, "MatchScores");
        AddSnapshotKeyColumns(migrationBuilder, "AiScoreMatchScores");
        AddSnapshotKeyColumns(migrationBuilder, "SofaScoreMatchScores");

        BackfillMatchLocalDate(migrationBuilder, "MatchScores");
        BackfillMatchLocalDate(migrationBuilder, "AiScoreMatchScores");
        BackfillMatchLocalDate(migrationBuilder, "SofaScoreMatchScores");

        DropLegacyFixtureIndex(migrationBuilder, "MatchScores", "IX_MatchScores_MatchTime_HomeTeam_AwayTeam");
        DropLegacyFixtureIndex(migrationBuilder, "AiScoreMatchScores", "IX_AiScoreMatchScores_MatchTime_HomeTeam_AwayTeam");
        DropLegacyFixtureIndex(migrationBuilder, "SofaScoreMatchScores", "IX_SofaScoreMatchScores_MatchTime_HomeTeam_AwayTeam");

        CreateSnapshotKeyIndex(migrationBuilder, "MatchScores", "IX_MatchScores_MatchLocalDate_HomeTeamKey_AwayTeamKey_LeagueKey");
        CreateSnapshotKeyIndex(migrationBuilder, "AiScoreMatchScores", "IX_AiScoreMatchScores_MatchLocalDate_HomeTeamKey_AwayTeamKey_LeagueKey");
        CreateSnapshotKeyIndex(migrationBuilder, "SofaScoreMatchScores", "IX_SofaScoreMatchScores_MatchLocalDate_HomeTeamKey_AwayTeamKey_LeagueKey");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        DropSnapshotKeyIndex(migrationBuilder, "MatchScores", "IX_MatchScores_MatchLocalDate_HomeTeamKey_AwayTeamKey_LeagueKey");
        DropSnapshotKeyIndex(migrationBuilder, "AiScoreMatchScores", "IX_AiScoreMatchScores_MatchLocalDate_HomeTeamKey_AwayTeamKey_LeagueKey");
        DropSnapshotKeyIndex(migrationBuilder, "SofaScoreMatchScores", "IX_SofaScoreMatchScores_MatchLocalDate_HomeTeamKey_AwayTeamKey_LeagueKey");

        migrationBuilder.CreateIndex(
            name: "IX_MatchScores_MatchTime_HomeTeam_AwayTeam",
            table: "MatchScores",
            columns: new[] { "MatchTime", "HomeTeam", "AwayTeam" });

        migrationBuilder.CreateIndex(
            name: "IX_AiScoreMatchScores_MatchTime_HomeTeam_AwayTeam",
            table: "AiScoreMatchScores",
            columns: new[] { "MatchTime", "HomeTeam", "AwayTeam" });

        migrationBuilder.CreateIndex(
            name: "IX_SofaScoreMatchScores_MatchTime_HomeTeam_AwayTeam",
            table: "SofaScoreMatchScores",
            columns: new[] { "MatchTime", "HomeTeam", "AwayTeam" });

        DropSnapshotKeyColumns(migrationBuilder, "MatchScores");
        DropSnapshotKeyColumns(migrationBuilder, "AiScoreMatchScores");
        DropSnapshotKeyColumns(migrationBuilder, "SofaScoreMatchScores");
    }

    private static void AddSnapshotKeyColumns(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "MatchLocalDate",
            table: table,
            type: "date",
            nullable: false,
            defaultValue: new DateOnly(1, 1, 1));

        migrationBuilder.AddColumn<string>(
            name: "HomeTeamKey",
            table: table,
            type: "character varying(512)",
            maxLength: 512,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "AwayTeamKey",
            table: table,
            type: "character varying(512)",
            maxLength: 512,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "LeagueKey",
            table: table,
            type: "character varying(512)",
            maxLength: 512,
            nullable: false,
            defaultValue: "");
    }

    private static void BackfillMatchLocalDate(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            UPDATE "{table}"
            SET "MatchLocalDate" = (timezone('Africa/Lagos', "MatchTime" AT TIME ZONE 'UTC'))::date;
            """);
    }

    private static void DropLegacyFixtureIndex(MigrationBuilder migrationBuilder, string table, string indexName)
    {
        migrationBuilder.DropIndex(
            name: indexName,
            table: table);
    }

    private static void CreateSnapshotKeyIndex(MigrationBuilder migrationBuilder, string table, string indexName)
    {
        migrationBuilder.CreateIndex(
            name: indexName,
            table: table,
            columns: new[] { "MatchLocalDate", "HomeTeamKey", "AwayTeamKey", "LeagueKey" });
    }

    private static void DropSnapshotKeyIndex(MigrationBuilder migrationBuilder, string table, string indexName)
    {
        migrationBuilder.DropIndex(
            name: indexName,
            table: table);
    }

    private static void DropSnapshotKeyColumns(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.DropColumn(name: "LeagueKey", table: table);
        migrationBuilder.DropColumn(name: "AwayTeamKey", table: table);
        migrationBuilder.DropColumn(name: "HomeTeamKey", table: table);
        migrationBuilder.DropColumn(name: "MatchLocalDate", table: table);
    }
}
