using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEnsembleWeightProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnsembleWeightProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    BookmakerWeight = table.Column<double>(type: "double precision", nullable: false),
                    CalculatorWeight = table.Column<double>(type: "double precision", nullable: false),
                    DixonColesWeight = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    HoldoutCount = table.Column<int>(type: "integer", nullable: false),
                    BaselineHoldoutBrier = table.Column<double>(type: "double precision", nullable: false),
                    CandidateHoldoutBrier = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnsembleWeightProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnsembleWeightProfiles_Market",
                table: "EnsembleWeightProfiles",
                column: "Market",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnsembleWeightProfiles");
        }
    }
}
