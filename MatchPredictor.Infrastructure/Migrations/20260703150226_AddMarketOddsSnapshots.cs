using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketOddsSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketOddsSnapshots",
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
                    SourceName = table.Column<string>(type: "text", nullable: false),
                    HomeWinOdds = table.Column<double>(type: "double precision", nullable: true),
                    DrawOdds = table.Column<double>(type: "double precision", nullable: true),
                    AwayWinOdds = table.Column<double>(type: "double precision", nullable: true),
                    Over25Odds = table.Column<double>(type: "double precision", nullable: true),
                    Under25Odds = table.Column<double>(type: "double precision", nullable: true),
                    BttsYesOdds = table.Column<double>(type: "double precision", nullable: true),
                    BttsNoOdds = table.Column<double>(type: "double precision", nullable: true),
                    FairHomeWin = table.Column<double>(type: "double precision", nullable: true),
                    FairDraw = table.Column<double>(type: "double precision", nullable: true),
                    FairAwayWin = table.Column<double>(type: "double precision", nullable: true),
                    FairOver25 = table.Column<double>(type: "double precision", nullable: true),
                    FairUnder25 = table.Column<double>(type: "double precision", nullable: true),
                    FairBttsYes = table.Column<double>(type: "double precision", nullable: true),
                    FairBttsNo = table.Column<double>(type: "double precision", nullable: true),
                    DeVigMethod = table.Column<string>(type: "text", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketOddsSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketOddsSnapshots_CapturedAtUtc",
                table: "MarketOddsSnapshots",
                column: "CapturedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MarketOddsSnapshots_MatchLocalDate_FixtureKey_SourceName",
                table: "MarketOddsSnapshots",
                columns: new[] { "MatchLocalDate", "FixtureKey", "SourceName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketOddsSnapshots");
        }
    }
}
