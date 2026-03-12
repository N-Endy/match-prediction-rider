using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddForecastObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForecastObservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<string>(type: "text", nullable: false),
                    Time = table.Column<string>(type: "text", nullable: false),
                    MatchDateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    League = table.Column<string>(type: "text", nullable: false),
                    HomeTeam = table.Column<string>(type: "text", nullable: false),
                    AwayTeam = table.Column<string>(type: "text", nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    PredictedOutcome = table.Column<string>(type: "text", nullable: false),
                    RawProbability = table.Column<double>(type: "double precision", nullable: false),
                    CalibratedProbability = table.Column<double>(type: "double precision", nullable: false),
                    OutcomeOccurred = table.Column<bool>(type: "boolean", nullable: true),
                    ActualOutcome = table.Column<string>(type: "text", nullable: true),
                    ActualScore = table.Column<string>(type: "text", nullable: true),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    IsLive = table.Column<bool>(type: "boolean", nullable: false),
                    IsSettled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SettledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastObservations_Date_HomeTeam_AwayTeam_League_Market",
                table: "ForecastObservations",
                columns: new[] { "Date", "HomeTeam", "AwayTeam", "League", "Market" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForecastObservations");
        }
    }
}
