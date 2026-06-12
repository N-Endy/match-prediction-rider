using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260611120000_AddForecastCorrectedProbability")]
public partial class AddForecastCorrectedProbability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<double>(
            name: "CorrectedProbability",
            table: "ForecastObservations",
            type: "double precision",
            nullable: false,
            defaultValue: 0.0);

        // Historical rows stored the meta-corrected value in RawProbability (the old
        // feedback-loop bug), so the corrected value IS that stored value. Backfill it
        // so calibration retraining on corrected inputs stays consistent.
        migrationBuilder.Sql("""UPDATE "ForecastObservations" SET "CorrectedProbability" = "RawProbability";""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CorrectedProbability",
            table: "ForecastObservations");
    }
}
