using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchPredictor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchDateTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MatchDateTime",
                table: "MatchDatas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MatchDateTime",
                table: "Predictions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE ""MatchDatas""
SET ""MatchDateTime"" = 
    to_timestamp(""Date"" || ' ' || COALESCE(""Time"", '00:00'), 'DD-MM-YYYY HH24:MI')
WHERE ""Date"" IS NOT NULL AND ""Date"" <> '';

UPDATE ""Predictions""
SET ""MatchDateTime"" = 
    to_timestamp(""Date"" || ' ' || COALESCE(""Time"", '00:00'), 'DD-MM-YYYY HH24:MI')
WHERE ""Date"" IS NOT NULL AND ""Date"" <> '';
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchDateTime",
                table: "MatchDatas");

            migrationBuilder.DropColumn(
                name: "MatchDateTime",
                table: "Predictions");
        }
    }
}

