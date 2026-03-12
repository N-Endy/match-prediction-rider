using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPredictionDecisionProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BaselineBrierScore",
                table: "ThresholdProfiles",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "BaselineHitRate",
                table: "ThresholdProfiles",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "BaselineThreshold",
                table: "ThresholdProfiles",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Improvement",
                table: "ThresholdProfiles",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "IsPromoted",
                table: "ThresholdProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TrainingSampleCount",
                table: "ThresholdProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ValidationSampleCount",
                table: "ThresholdProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CalibratorUsed",
                table: "Predictions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ThresholdSource",
                table: "Predictions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "ThresholdUsed",
                table: "Predictions",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "WasPublished",
                table: "Predictions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CalibratorUsed",
                table: "ForecastObservations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ThresholdSource",
                table: "ForecastObservations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "ThresholdUsed",
                table: "ForecastObservations",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineBrierScore",
                table: "ThresholdProfiles");

            migrationBuilder.DropColumn(
                name: "BaselineHitRate",
                table: "ThresholdProfiles");

            migrationBuilder.DropColumn(
                name: "BaselineThreshold",
                table: "ThresholdProfiles");

            migrationBuilder.DropColumn(
                name: "Improvement",
                table: "ThresholdProfiles");

            migrationBuilder.DropColumn(
                name: "IsPromoted",
                table: "ThresholdProfiles");

            migrationBuilder.DropColumn(
                name: "TrainingSampleCount",
                table: "ThresholdProfiles");

            migrationBuilder.DropColumn(
                name: "ValidationSampleCount",
                table: "ThresholdProfiles");

            migrationBuilder.DropColumn(
                name: "CalibratorUsed",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "ThresholdSource",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "ThresholdUsed",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "WasPublished",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "CalibratorUsed",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "ThresholdSource",
                table: "ForecastObservations");

            migrationBuilder.DropColumn(
                name: "ThresholdUsed",
                table: "ForecastObservations");
        }
    }
}
