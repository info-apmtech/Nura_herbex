using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerCare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ConvertedByEmployeeId",
                table: "leads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomerSince",
                table: "leads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WelcomeSentAt",
                table: "leads",
                type: "datetime2",
                nullable: true);

            // People already marked Converted become customers (kept with their current owner) and do not get a welcome message now.
            migrationBuilder.Sql(@"UPDATE leads
SET CustomerSince = COALESCE(ConvertedAt, CreatedAt),
    ConvertedByEmployeeId = AssignedToEmployeeId,
    WelcomeSentAt = SYSUTCDATETIME()
WHERE Status = 'converted' AND CustomerSince IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConvertedByEmployeeId",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "CustomerSince",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "WelcomeSentAt",
                table: "leads");
        }
    }
}
