using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260820140000_AddAiScoreRegularTimeScore")]
public partial class AddAiScoreRegularTimeScore : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RegularTimeScore",
            table: "AiScoreMatchScores",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RegularTimeScore",
            table: "AiScoreMatchScores");
    }
}
