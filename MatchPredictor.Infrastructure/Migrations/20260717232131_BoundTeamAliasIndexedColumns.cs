using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

/// <inheritdoc />
public partial class BoundTeamAliasIndexedColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Drop unique indexes before truncating so oversized/duplicate rows can be cleaned up.
        migrationBuilder.DropIndex(
            name: "IX_Teams_NormalizedName_LeagueScope",
            table: "Teams");

        migrationBuilder.DropIndex(
            name: "IX_TeamAliases_NormalizedAlias_LeagueScope_SourceName",
            table: "TeamAliases");

        migrationBuilder.Sql("""
            UPDATE "Teams"
            SET
                "NormalizedName" = LEFT("NormalizedName", 256),
                "Name" = LEFT("Name", 256),
                "LeagueScope" = CASE
                    WHEN "LeagueScope" IS NULL THEN NULL
                    ELSE LEFT("LeagueScope", 256)
                END;

            UPDATE "TeamAliases"
            SET
                "NormalizedAlias" = LEFT("NormalizedAlias", 256),
                "Alias" = LEFT("Alias", 256),
                "SourceName" = LEFT("SourceName", 256),
                "LeagueScope" = CASE
                    WHEN "LeagueScope" IS NULL THEN NULL
                    ELSE LEFT("LeagueScope", 256)
                END;

            -- Keep the oldest Team row per truncated unique key.
            WITH ranked_teams AS (
                SELECT
                    "Id",
                    ROW_NUMBER() OVER (
                        PARTITION BY "NormalizedName", "LeagueScope"
                        ORDER BY "Id"
                    ) AS rn
                FROM "Teams"
            )
            DELETE FROM "Teams" t
            USING ranked_teams r
            WHERE t."Id" = r."Id"
              AND r.rn > 1;

            -- Keep the oldest TeamAlias row per truncated unique key.
            WITH ranked_aliases AS (
                SELECT
                    "Id",
                    ROW_NUMBER() OVER (
                        PARTITION BY "NormalizedAlias", "LeagueScope", "SourceName"
                        ORDER BY "Id"
                    ) AS rn
                FROM "TeamAliases"
            )
            DELETE FROM "TeamAliases" a
            USING ranked_aliases r
            WHERE a."Id" = r."Id"
              AND r.rn > 1;
            """);

        migrationBuilder.AlterColumn<string>(
            name: "NormalizedName",
            table: "Teams",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");

        migrationBuilder.AlterColumn<string>(
            name: "Name",
            table: "Teams",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");

        migrationBuilder.AlterColumn<string>(
            name: "LeagueScope",
            table: "Teams",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "SourceName",
            table: "TeamAliases",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");

        migrationBuilder.AlterColumn<string>(
            name: "NormalizedAlias",
            table: "TeamAliases",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");

        migrationBuilder.AlterColumn<string>(
            name: "LeagueScope",
            table: "TeamAliases",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "Alias",
            table: "TeamAliases",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");

        migrationBuilder.CreateIndex(
            name: "IX_Teams_NormalizedName_LeagueScope",
            table: "Teams",
            columns: new[] { "NormalizedName", "LeagueScope" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TeamAliases_NormalizedAlias_LeagueScope_SourceName",
            table: "TeamAliases",
            columns: new[] { "NormalizedAlias", "LeagueScope", "SourceName" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "NormalizedName",
            table: "Teams",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256);

        migrationBuilder.AlterColumn<string>(
            name: "Name",
            table: "Teams",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256);

        migrationBuilder.AlterColumn<string>(
            name: "LeagueScope",
            table: "Teams",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "SourceName",
            table: "TeamAliases",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256);

        migrationBuilder.AlterColumn<string>(
            name: "NormalizedAlias",
            table: "TeamAliases",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256);

        migrationBuilder.AlterColumn<string>(
            name: "LeagueScope",
            table: "TeamAliases",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "Alias",
            table: "TeamAliases",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256);
    }
}
