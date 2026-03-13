using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

public partial class AddMatchDataBttsProbabilities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<double>(
            name: "BttsNo",
            table: "MatchDatas",
            type: "double precision",
            nullable: false,
            defaultValue: 0.0);

        migrationBuilder.AddColumn<double>(
            name: "BttsYes",
            table: "MatchDatas",
            type: "double precision",
            nullable: false,
            defaultValue: 0.0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BttsNo",
            table: "MatchDatas");

        migrationBuilder.DropColumn(
            name: "BttsYes",
            table: "MatchDatas");
    }
}
