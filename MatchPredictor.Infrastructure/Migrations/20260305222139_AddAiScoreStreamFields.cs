using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiScoreStreamFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiScoreMatchId",
                table: "Predictions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasStream",
                table: "Predictions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiScoreMatchId",
                table: "MatchScores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasStream",
                table: "MatchScores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiScoreMatchId",
                table: "AiScoreMatchScores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasStream",
                table: "AiScoreMatchScores",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiScoreMatchId",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "HasStream",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "AiScoreMatchId",
                table: "MatchScores");

            migrationBuilder.DropColumn(
                name: "HasStream",
                table: "MatchScores");

            migrationBuilder.DropColumn(
                name: "AiScoreMatchId",
                table: "AiScoreMatchScores");

            migrationBuilder.DropColumn(
                name: "HasStream",
                table: "AiScoreMatchScores");
        }
    }
}
