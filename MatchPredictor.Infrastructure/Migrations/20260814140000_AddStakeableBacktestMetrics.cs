using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260814140000_AddStakeableBacktestMetrics")]
public partial class AddStakeableBacktestMetrics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "StakeableBetCount",
            table: "HistoricalBacktestSummaries",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<double>(
            name: "StakeableFlatStakeRoiPercent",
            table: "HistoricalBacktestSummaries",
            type: "double precision",
            nullable: false,
            defaultValue: 0.0);

        migrationBuilder.AddColumn<double>(
            name: "StakeableAverageClvPercent",
            table: "HistoricalBacktestSummaries",
            type: "double precision",
            nullable: false,
            defaultValue: 0.0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "StakeableBetCount",
            table: "HistoricalBacktestSummaries");

        migrationBuilder.DropColumn(
            name: "StakeableFlatStakeRoiPercent",
            table: "HistoricalBacktestSummaries");

        migrationBuilder.DropColumn(
            name: "StakeableAverageClvPercent",
            table: "HistoricalBacktestSummaries");
    }
}
