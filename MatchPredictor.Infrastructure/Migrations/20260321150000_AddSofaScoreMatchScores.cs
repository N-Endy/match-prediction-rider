using System;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260321150000_AddSofaScoreMatchScores")]
public partial class AddSofaScoreMatchScores : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SofaScoreMatchScores",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                League = table.Column<string>(type: "text", nullable: false),
                HomeTeam = table.Column<string>(type: "text", nullable: false),
                AwayTeam = table.Column<string>(type: "text", nullable: false),
                Score = table.Column<string>(type: "text", nullable: false),
                DisplayedScore = table.Column<string>(type: "text", nullable: true),
                RegularTimeScore = table.Column<string>(type: "text", nullable: true),
                HalfTimeScore = table.Column<string>(type: "text", nullable: true),
                ExtraTimeScore = table.Column<string>(type: "text", nullable: true),
                StatusText = table.Column<string>(type: "text", nullable: true),
                EventUrl = table.Column<string>(type: "text", nullable: false),
                MatchTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                BTTSLabel = table.Column<bool>(type: "boolean", nullable: false),
                IsLive = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SofaScoreMatchScores", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SofaScoreMatchScores_MatchTime_HomeTeam_AwayTeam",
            table: "SofaScoreMatchScores",
            columns: new[] { "MatchTime", "HomeTeam", "AwayTeam" });

        migrationBuilder.CreateIndex(
            name: "IX_SofaScoreMatchScores_MatchTime_IsLive",
            table: "SofaScoreMatchScores",
            columns: new[] { "MatchTime", "IsLive" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SofaScoreMatchScores");
    }
}
