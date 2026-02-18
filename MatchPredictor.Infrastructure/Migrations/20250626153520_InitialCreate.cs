using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MatchDatas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<string>(type: "TEXT", nullable: true),
                    League = table.Column<string>(type: "TEXT", nullable: true),
                    HomeTeam = table.Column<string>(type: "TEXT", nullable: true),
                    AwayTeam = table.Column<string>(type: "TEXT", nullable: true),
                    HomeWin = table.Column<double>(type: "REAL", nullable: false),
                    Draw = table.Column<double>(type: "REAL", nullable: false),
                    AwayWin = table.Column<double>(type: "REAL", nullable: false),
                    OverTwoGoals = table.Column<double>(type: "REAL", nullable: false),
                    OverThreeGoals = table.Column<double>(type: "REAL", nullable: false),
                    UnderTwoGoals = table.Column<double>(type: "REAL", nullable: false),
                    UnderThreeGoals = table.Column<double>(type: "REAL", nullable: false),
                    OverFourGoals = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchDatas", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchDatas");
        }
    }
}
