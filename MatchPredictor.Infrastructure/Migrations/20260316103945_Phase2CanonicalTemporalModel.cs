using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase2CanonicalTemporalModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Predictions_Date_HomeTeam_AwayTeam_League",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_Date_IsLive",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_Date_PredictionCategory_Time",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_Date_HomeTeam_AwayTeam",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_Date_League",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_Date_Time",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_ForecastObservations_Date_HomeTeam_AwayTeam_League_Market",
                table: "ForecastObservations");

            migrationBuilder.AddColumn<string>(
                name: "FixtureKey",
                table: "Predictions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrentRevision",
                table: "Predictions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "MatchLocalDate",
                table: "Predictions",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "MatchLocalTime",
                table: "Predictions",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PredictionRunId",
                table: "Predictions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "RevisionNumber",
                table: "Predictions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RunLabel",
                table: "Predictions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RunReason",
                table: "Predictions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAt",
                table: "Predictions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FixtureKey",
                table: "MatchDatas",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "MatchLocalDate",
                table: "MatchDatas",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "MatchLocalTime",
                table: "MatchDatas",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FixtureKey",
                table: "ForecastObservations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrentRevision",
                table: "ForecastObservations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "MatchLocalDate",
                table: "ForecastObservations",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "MatchLocalTime",
                table: "ForecastObservations",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PredictionRunId",
                table: "ForecastObservations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "RevisionNumber",
                table: "ForecastObservations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RunLabel",
                table: "ForecastObservations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RunReason",
                table: "ForecastObservations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAt",
                table: "ForecastObservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PredictionRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunKind = table.Column<string>(type: "text", nullable: false),
                    RunLabel = table.Column<string>(type: "text", nullable: false),
                    RunReason = table.Column<string>(type: "text", nullable: false),
                    TargetLocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ForecastCount = table.Column<int>(type: "integer", nullable: false),
                    PublishedPredictionCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictionRuns", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                UPDATE "MatchDatas"
                SET "MatchLocalDate" = COALESCE(
                        CASE
                            WHEN "MatchDateTime" IS NOT NULL THEN timezone('Africa/Lagos', "MatchDateTime")::date
                        END,
                        CASE
                            WHEN NULLIF(btrim("Date"), '') ~ '^\d{2}-\d{2}-\d{4}$' THEN to_date(NULLIF(btrim("Date"), ''), 'DD-MM-YYYY')
                        END),
                    "MatchLocalTime" = COALESCE(
                        CASE
                            WHEN "MatchDateTime" IS NOT NULL THEN timezone('Africa/Lagos', "MatchDateTime")::time
                        END,
                        CASE
                            WHEN NULLIF(btrim("Time"), '') ~ '^\d{1,2}:\d{2}(:\d{2})?$' THEN NULLIF(btrim("Time"), '')::time
                        END);

                UPDATE "MatchDatas"
                SET "FixtureKey" =
                    lower(COALESCE(to_char("MatchLocalDate", 'DD-MM-YYYY'), 'unknown-date')) || '|' ||
                    regexp_replace(lower(btrim(COALESCE("League", ''))), '\s+', ' ', 'g') || '|' ||
                    regexp_replace(lower(btrim(COALESCE("HomeTeam", ''))), '\s+', ' ', 'g') || '|' ||
                    regexp_replace(lower(btrim(COALESCE("AwayTeam", ''))), '\s+', ' ', 'g');

                UPDATE "Predictions"
                SET "MatchLocalDate" = COALESCE(
                        CASE
                            WHEN "MatchDateTime" IS NOT NULL THEN timezone('Africa/Lagos', "MatchDateTime")::date
                        END,
                        CASE
                            WHEN NULLIF(btrim("Date"), '') ~ '^\d{2}-\d{2}-\d{4}$' THEN to_date(NULLIF(btrim("Date"), ''), 'DD-MM-YYYY')
                        END,
                        DATE '0001-01-01'),
                    "MatchLocalTime" = COALESCE(
                        CASE
                            WHEN "MatchDateTime" IS NOT NULL THEN timezone('Africa/Lagos', "MatchDateTime")::time
                        END,
                        CASE
                            WHEN NULLIF(btrim("Time"), '') ~ '^\d{1,2}:\d{2}(:\d{2})?$' THEN NULLIF(btrim("Time"), '')::time
                        END),
                    "FixtureKey" =
                        lower(COALESCE(
                            CASE
                                WHEN "MatchDateTime" IS NOT NULL THEN to_char(timezone('Africa/Lagos', "MatchDateTime")::date, 'DD-MM-YYYY')
                            END,
                            CASE
                                WHEN NULLIF(btrim("Date"), '') ~ '^\d{2}-\d{2}-\d{4}$' THEN NULLIF(btrim("Date"), '')
                            END,
                            'unknown-date')) || '|' ||
                        regexp_replace(lower(btrim(COALESCE("League", ''))), '\s+', ' ', 'g') || '|' ||
                        regexp_replace(lower(btrim(COALESCE("HomeTeam", ''))), '\s+', ' ', 'g') || '|' ||
                        regexp_replace(lower(btrim(COALESCE("AwayTeam", ''))), '\s+', ' ', 'g'),
                    "IsCurrentRevision" = TRUE,
                    "RevisionNumber" = 1,
                    "RunLabel" = 'Legacy Snapshot',
                    "RunReason" = 'phase2-migration',
                    "PredictionRunId" = (
                        substr(md5('prediction:' || "Id"::text), 1, 8) || '-' ||
                        substr(md5('prediction:' || "Id"::text), 9, 4) || '-' ||
                        substr(md5('prediction:' || "Id"::text), 13, 4) || '-' ||
                        substr(md5('prediction:' || "Id"::text), 17, 4) || '-' ||
                        substr(md5('prediction:' || "Id"::text), 21, 12)
                    )::uuid;

                UPDATE "ForecastObservations"
                SET "MatchLocalDate" = COALESCE(
                        CASE
                            WHEN "MatchDateTime" IS NOT NULL THEN timezone('Africa/Lagos', "MatchDateTime")::date
                        END,
                        CASE
                            WHEN NULLIF(btrim("Date"), '') ~ '^\d{2}-\d{2}-\d{4}$' THEN to_date(NULLIF(btrim("Date"), ''), 'DD-MM-YYYY')
                        END,
                        DATE '0001-01-01'),
                    "MatchLocalTime" = COALESCE(
                        CASE
                            WHEN "MatchDateTime" IS NOT NULL THEN timezone('Africa/Lagos', "MatchDateTime")::time
                        END,
                        CASE
                            WHEN NULLIF(btrim("Time"), '') ~ '^\d{1,2}:\d{2}(:\d{2})?$' THEN NULLIF(btrim("Time"), '')::time
                        END),
                    "FixtureKey" =
                        lower(COALESCE(
                            CASE
                                WHEN "MatchDateTime" IS NOT NULL THEN to_char(timezone('Africa/Lagos', "MatchDateTime")::date, 'DD-MM-YYYY')
                            END,
                            CASE
                                WHEN NULLIF(btrim("Date"), '') ~ '^\d{2}-\d{2}-\d{4}$' THEN NULLIF(btrim("Date"), '')
                            END,
                            'unknown-date')) || '|' ||
                        regexp_replace(lower(btrim(COALESCE("League", ''))), '\s+', ' ', 'g') || '|' ||
                        regexp_replace(lower(btrim(COALESCE("HomeTeam", ''))), '\s+', ' ', 'g') || '|' ||
                        regexp_replace(lower(btrim(COALESCE("AwayTeam", ''))), '\s+', ' ', 'g'),
                    "IsCurrentRevision" = TRUE,
                    "RevisionNumber" = 1,
                    "RunLabel" = 'Legacy Snapshot',
                    "RunReason" = 'phase2-migration',
                    "PredictionRunId" = (
                        substr(md5('forecast:' || "Id"::text), 1, 8) || '-' ||
                        substr(md5('forecast:' || "Id"::text), 9, 4) || '-' ||
                        substr(md5('forecast:' || "Id"::text), 13, 4) || '-' ||
                        substr(md5('forecast:' || "Id"::text), 17, 4) || '-' ||
                        substr(md5('forecast:' || "Id"::text), 21, 12)
                    )::uuid;

                INSERT INTO "PredictionRuns" (
                    "Id",
                    "RunKind",
                    "RunLabel",
                    "RunReason",
                    "TargetLocalDate",
                    "StartedAtUtc",
                    "CompletedAtUtc",
                    "Succeeded",
                    "ForecastCount",
                    "PublishedPredictionCount")
                SELECT
                    "PredictionRunId",
                    'legacy_migration',
                    "RunLabel",
                    "RunReason",
                    "MatchLocalDate",
                    COALESCE("CreatedAt", NOW()),
                    COALESCE("CreatedAt", NOW()),
                    TRUE,
                    0,
                    CASE WHEN "WasPublished" THEN 1 ELSE 0 END
                FROM "Predictions";

                INSERT INTO "PredictionRuns" (
                    "Id",
                    "RunKind",
                    "RunLabel",
                    "RunReason",
                    "TargetLocalDate",
                    "StartedAtUtc",
                    "CompletedAtUtc",
                    "Succeeded",
                    "ForecastCount",
                    "PublishedPredictionCount")
                SELECT
                    "PredictionRunId",
                    'legacy_migration',
                    "RunLabel",
                    "RunReason",
                    "MatchLocalDate",
                    COALESCE("CreatedAt", NOW()),
                    COALESCE("CreatedAt", NOW()),
                    TRUE,
                    1,
                    0
                FROM "ForecastObservations";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_FixtureKey_IsCurrentRevision",
                table: "Predictions",
                columns: new[] { "FixtureKey", "IsCurrentRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_MatchLocalDate_IsCurrentRevision_League",
                table: "Predictions",
                columns: new[] { "MatchLocalDate", "IsCurrentRevision", "League" });

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_MatchLocalDate_IsCurrentRevision_PredictionCate~",
                table: "Predictions",
                columns: new[] { "MatchLocalDate", "IsCurrentRevision", "PredictionCategory", "MatchLocalTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_PredictionRunId_FixtureKey_PredictionCategory",
                table: "Predictions",
                columns: new[] { "PredictionRunId", "FixtureKey", "PredictionCategory" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_PredictionRunId_PredictionCategory",
                table: "Predictions",
                columns: new[] { "PredictionRunId", "PredictionCategory" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_FixtureKey",
                table: "MatchDatas",
                column: "FixtureKey");

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_MatchLocalDate_League",
                table: "MatchDatas",
                columns: new[] { "MatchLocalDate", "League" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_MatchLocalDate_MatchLocalTime",
                table: "MatchDatas",
                columns: new[] { "MatchLocalDate", "MatchLocalTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastObservations_FixtureKey_IsCurrentRevision_Market",
                table: "ForecastObservations",
                columns: new[] { "FixtureKey", "IsCurrentRevision", "Market" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastObservations_MatchLocalDate_IsCurrentRevision_Marke~",
                table: "ForecastObservations",
                columns: new[] { "MatchLocalDate", "IsCurrentRevision", "Market", "MatchLocalTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastObservations_PredictionRunId_FixtureKey_Market",
                table: "ForecastObservations",
                columns: new[] { "PredictionRunId", "FixtureKey", "Market" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForecastObservations_PredictionRunId_Market",
                table: "ForecastObservations",
                columns: new[] { "PredictionRunId", "Market" });

            migrationBuilder.CreateIndex(
                name: "IX_PredictionRuns_RunKind_StartedAtUtc",
                table: "PredictionRuns",
                columns: new[] { "RunKind", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PredictionRuns_TargetLocalDate_StartedAtUtc",
                table: "PredictionRuns",
                columns: new[] { "TargetLocalDate", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PredictionRuns");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_FixtureKey_IsCurrentRevision",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_MatchLocalDate_IsCurrentRevision_League",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_MatchLocalDate_IsCurrentRevision_PredictionCate~",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_PredictionRunId_FixtureKey_PredictionCategory",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_Predictions_PredictionRunId_PredictionCategory",
                table: "Predictions");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_FixtureKey",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_MatchLocalDate_League",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_MatchDatas_MatchLocalDate_MatchLocalTime",
                table: "MatchDatas");

            migrationBuilder.DropIndex(
                name: "IX_ForecastObservations_FixtureKey_IsCurrentRevision_Market",
                table: "ForecastObservations");

            migrationBuilder.DropIndex(
                name: "IX_ForecastObservations_MatchLocalDate_IsCurrentRevision_Marke~",
                table: "ForecastObservations");

            migrationBuilder.DropIndex(
                name: "IX_ForecastObservations_PredictionRunId_FixtureKey_Market",
                table: "ForecastObservations");

            migrationBuilder.DropIndex(
                name: "IX_ForecastObservations_PredictionRunId_Market",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "FixtureKey",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "IsCurrentRevision",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "MatchLocalDate",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "MatchLocalTime",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "PredictionRunId",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "RevisionNumber",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "RunLabel",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "RunReason",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "FixtureKey",
                table: "MatchDatas");

            migrationBuilder.DropColumn(
                name: "MatchLocalDate",
                table: "MatchDatas");

            migrationBuilder.DropColumn(
                name: "MatchLocalTime",
                table: "MatchDatas");

            migrationBuilder.DropColumn(
                name: "FixtureKey",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "IsCurrentRevision",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "MatchLocalDate",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "MatchLocalTime",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "PredictionRunId",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "RevisionNumber",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "RunLabel",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "RunReason",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "ForecastObservations");

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_Date_HomeTeam_AwayTeam_League",
                table: "Predictions",
                columns: new[] { "Date", "HomeTeam", "AwayTeam", "League" });

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_Date_IsLive",
                table: "Predictions",
                columns: new[] { "Date", "IsLive" });

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_Date_PredictionCategory_Time",
                table: "Predictions",
                columns: new[] { "Date", "PredictionCategory", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_Date_HomeTeam_AwayTeam",
                table: "MatchDatas",
                columns: new[] { "Date", "HomeTeam", "AwayTeam" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_Date_League",
                table: "MatchDatas",
                columns: new[] { "Date", "League" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchDatas_Date_Time",
                table: "MatchDatas",
                columns: new[] { "Date", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastObservations_Date_HomeTeam_AwayTeam_League_Market",
                table: "ForecastObservations",
                columns: new[] { "Date", "HomeTeam", "AwayTeam", "League", "Market" },
                unique: true);
        }
    }
}
