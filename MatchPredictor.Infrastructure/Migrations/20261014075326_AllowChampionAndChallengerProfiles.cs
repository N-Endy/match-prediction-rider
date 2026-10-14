using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowChampionAndChallengerProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketMlModelProfiles_Market",
                table: "MarketMlModelProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_MarketMlModelProfiles_Market_IsPromoted_IsShadow",
                table: "MarketMlModelProfiles",
                columns: new[] { "Market", "IsPromoted", "IsShadow" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketMlModelProfiles_Market_Promoted",
                table: "MarketMlModelProfiles",
                column: "Market",
                unique: true,
                filter: "\"IsPromoted\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_MarketMlModelProfiles_Market_Shadow",
                table: "MarketMlModelProfiles",
                column: "Market",
                unique: true,
                filter: "\"IsShadow\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketMlModelProfiles_Market_IsPromoted_IsShadow",
                table: "MarketMlModelProfiles");

            migrationBuilder.DropIndex(
                name: "IX_MarketMlModelProfiles_Market_Promoted",
                table: "MarketMlModelProfiles");

            migrationBuilder.DropIndex(
                name: "IX_MarketMlModelProfiles_Market_Shadow",
                table: "MarketMlModelProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_MarketMlModelProfiles_Market",
                table: "MarketMlModelProfiles",
                column: "Market",
                unique: true);
        }
    }
}
