using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBlendWeightProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlendWeightProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    SourceWeight = table.Column<double>(type: "double precision", nullable: false),
                    SecondaryWeight = table.Column<double>(type: "double precision", nullable: false),
                    PoissonWeight = table.Column<double>(type: "double precision", nullable: false),
                    TrainingSampleCount = table.Column<int>(type: "integer", nullable: false),
                    ValidationSampleCount = table.Column<int>(type: "integer", nullable: false),
                    BaselineBrierScore = table.Column<double>(type: "double precision", nullable: false),
                    CandidateBrierScore = table.Column<double>(type: "double precision", nullable: false),
                    Improvement = table.Column<double>(type: "double precision", nullable: false),
                    IsPromoted = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlendWeightProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlendWeightProfiles_Market",
                table: "BlendWeightProfiles",
                column: "Market",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlendWeightProfiles");
        }
    }
}
