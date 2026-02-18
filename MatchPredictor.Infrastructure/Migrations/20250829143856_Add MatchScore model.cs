using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchScoremodel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActualOutcome",
                table: "Predictions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActualScore",
                table: "Predictions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MatchScores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    League = table.Column<string>(type: "text", nullable: false),
                    HomeTeam = table.Column<string>(type: "text", nullable: false),
                    AwayTeam = table.Column<string>(type: "text", nullable: false),
                    Score = table.Column<string>(type: "text", nullable: false),
                    MatchTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BTTSLabel = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchScores", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchScores");

            migrationBuilder.DropColumn(
                name: "ActualOutcome",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "ActualScore",
                table: "Predictions");
        }
    }
}
