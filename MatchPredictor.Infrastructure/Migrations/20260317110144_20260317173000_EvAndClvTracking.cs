using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class _20260317173000_EvAndClvTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PredictionOddsSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PredictionId = table.Column<int>(type: "integer", nullable: false),
                    PredictionRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceName = table.Column<string>(type: "text", nullable: false),
                    Market = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    DecimalOdds = table.Column<double>(type: "double precision", nullable: false),
                    ImpliedProbability = table.Column<double>(type: "double precision", nullable: false),
                    OddsDerivationSource = table.Column<string>(type: "text", nullable: false),
                    SnapshotKind = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictionOddsSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PredictionOddsSnapshots_PredictionId_SourceName_SnapshotKind",
                table: "PredictionOddsSnapshots",
                columns: new[] { "PredictionId", "SourceName", "SnapshotKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PredictionOddsSnapshots_PredictionRunId_SnapshotKind",
                table: "PredictionOddsSnapshots",
                columns: new[] { "PredictionRunId", "SnapshotKind" });

            migrationBuilder.CreateIndex(
                name: "IX_PredictionOddsSnapshots_SnapshotKind_CapturedAtUtc",
                table: "PredictionOddsSnapshots",
                columns: new[] { "SnapshotKind", "CapturedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PredictionOddsSnapshots");
        }
    }
}
