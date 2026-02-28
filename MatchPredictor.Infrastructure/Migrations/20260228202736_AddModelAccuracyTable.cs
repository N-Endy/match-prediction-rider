using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModelAccuracyTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelAccuracies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Category = table.Column<string>(type: "text", nullable: false),
                    MetricName = table.Column<string>(type: "text", nullable: false),
                    MetricRangeStart = table.Column<double>(type: "double precision", nullable: false),
                    MetricRangeEnd = table.Column<double>(type: "double precision", nullable: false),
                    TotalPredictions = table.Column<int>(type: "integer", nullable: false),
                    CorrectPredictions = table.Column<int>(type: "integer", nullable: false),
                    AccuracyPercentage = table.Column<double>(type: "double precision", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelAccuracies", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelAccuracies");
        }
    }
}
