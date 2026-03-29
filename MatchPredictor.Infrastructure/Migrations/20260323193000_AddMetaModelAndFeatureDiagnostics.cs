using System;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260323193000_AddMetaModelAndFeatureDiagnostics")]
public partial class AddMetaModelAndFeatureDiagnostics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FeatureContributionsJson",
            table: "ForecastObservations",
            type: "text",
            nullable: false,
            defaultValue: "{}");

        migrationBuilder.CreateTable(
            name: "MetaModelProfiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Market = table.Column<int>(type: "integer", nullable: false),
                Intercept = table.Column<double>(type: "double precision", nullable: false),
                Slope = table.Column<double>(type: "double precision", nullable: false),
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
                table.PrimaryKey("PK_MetaModelProfiles", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MetaModelProfiles_Market",
            table: "MetaModelProfiles",
            column: "Market",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MetaModelProfiles");

        migrationBuilder.DropColumn(
            name: "FeatureContributionsJson",
            table: "ForecastObservations");
    }
}
