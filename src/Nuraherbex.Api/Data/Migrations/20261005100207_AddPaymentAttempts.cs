using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TxnId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    GatewayStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayuPaymentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PaymentMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CustomerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CustomerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CustomerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OrderSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GatewayResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_CreatedAt",
                table: "payment_attempts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_OrderId",
                table: "payment_attempts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_TxnId",
                table: "payment_attempts",
                column: "TxnId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_attempts");
        }
    }
}
