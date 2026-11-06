using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ad_insights_daily",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    AdId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AdName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AdSetId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AdSetName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CampaignId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CampaignName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Spend = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Impressions = table.Column<long>(type: "bigint", nullable: false),
                    Clicks = table.Column<long>(type: "bigint", nullable: false),
                    Conversations = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ad_insights_daily", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ad_insights_daily_CampaignId",
                table: "ad_insights_daily",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_ad_insights_daily_Date_AdId",
                table: "ad_insights_daily",
                columns: new[] { "Date", "AdId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ad_insights_daily");
        }
    }
}
