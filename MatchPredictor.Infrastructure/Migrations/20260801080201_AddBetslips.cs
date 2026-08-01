using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBetslips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BetslipSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SlipLocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunLabel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DayKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    SlipCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BetslipSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Betslips",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BetslipSetId = table.Column<int>(type: "integer", nullable: false),
                    SlipNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TierLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetMinSelections = table.Column<int>(type: "integer", nullable: false),
                    TargetMaxSelections = table.Column<int>(type: "integer", nullable: false),
                    SelectionCount = table.Column<int>(type: "integer", nullable: false),
                    BookingCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BookingUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    BookingStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    EarliestKickoffUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CombinedDecimalOdds = table.Column<double>(type: "double precision", nullable: true),
                    AiSummary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Betslips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Betslips_BetslipSets_BetslipSetId",
                        column: x => x.BetslipSetId,
                        principalTable: "BetslipSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BetslipSelections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BetslipId = table.Column<int>(type: "integer", nullable: false),
                    PredictionId = table.Column<int>(type: "integer", nullable: true),
                    League = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    HomeTeam = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AwayTeam = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PredictedOutcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "numeric", nullable: true),
                    MatchDateTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecimalOdds = table.Column<double>(type: "double precision", nullable: true),
                    AiNote = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    WasBooked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BetslipSelections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BetslipSelections_Betslips_BetslipId",
                        column: x => x.BetslipId,
                        principalTable: "Betslips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Betslips_BetslipSetId_SlipNumber",
                table: "Betslips",
                columns: new[] { "BetslipSetId", "SlipNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BetslipSelections_BetslipId",
                table: "BetslipSelections",
                column: "BetslipId");

            migrationBuilder.CreateIndex(
                name: "IX_BetslipSelections_PredictionId",
                table: "BetslipSelections",
                column: "PredictionId");

            migrationBuilder.CreateIndex(
                name: "IX_BetslipSets_GeneratedAtUtc",
                table: "BetslipSets",
                column: "GeneratedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BetslipSets_SlipLocalDate_IsCurrent",
                table: "BetslipSets",
                columns: new[] { "SlipLocalDate", "IsCurrent" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BetslipSelections");

            migrationBuilder.DropTable(
                name: "Betslips");

            migrationBuilder.DropTable(
                name: "BetslipSets");
        }
    }
}
