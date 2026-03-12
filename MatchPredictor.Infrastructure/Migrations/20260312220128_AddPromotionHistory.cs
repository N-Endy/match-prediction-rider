using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PromotionHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    ChangeType = table.Column<string>(type: "text", nullable: false),
                    PreviousValue = table.Column<string>(type: "text", nullable: false),
                    NewValue = table.Column<string>(type: "text", nullable: false),
                    PreviousNumericValue = table.Column<double>(type: "double precision", nullable: true),
                    NewNumericValue = table.Column<double>(type: "double precision", nullable: true),
                    BaselineScore = table.Column<double>(type: "double precision", nullable: true),
                    CandidateScore = table.Column<double>(type: "double precision", nullable: true),
                    Improvement = table.Column<double>(type: "double precision", nullable: true),
                    EffectiveAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotionHistories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromotionHistories_EffectiveAt_Market",
                table: "PromotionHistories",
                columns: new[] { "EffectiveAt", "Market" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PromotionHistories");
        }
    }
}
