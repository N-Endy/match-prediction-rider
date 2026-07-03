using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketMlModelProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketMlModelProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    ModelBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    TrainingSampleCount = table.Column<int>(type: "integer", nullable: false),
                    HoldoutSampleCount = table.Column<int>(type: "integer", nullable: false),
                    BaselineBrierScore = table.Column<double>(type: "double precision", nullable: false),
                    CandidateBrierScore = table.Column<double>(type: "double precision", nullable: false),
                    Improvement = table.Column<double>(type: "double precision", nullable: false),
                    IsPromoted = table.Column<bool>(type: "boolean", nullable: false),
                    FeatureSchemaJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketMlModelProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketMlModelProfiles_Market",
                table: "MarketMlModelProfiles",
                column: "Market",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketMlModelProfiles");
        }
    }
}
