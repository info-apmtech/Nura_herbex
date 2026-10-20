using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShadowfaxFulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAt",
                table: "orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FulfillmentReadyAt",
                table: "orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastShippingEventAt",
                table: "orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastTrackingCheckedAt",
                table: "orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ParcelBreadthCm",
                table: "orders",
                type: "decimal(9,2)",
                precision: 9,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ParcelHeightCm",
                table: "orders",
                type: "decimal(9,2)",
                precision: 9,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ParcelLengthCm",
                table: "orders",
                type: "decimal(9,2)",
                precision: 9,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ParcelWeightKg",
                table: "orders",
                type: "decimal(9,3)",
                precision: 9,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "PickedUpAt",
                table: "orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ShipmentBookedAt",
                table: "orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShipmentClientOrderId",
                table: "orders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_Courier_ShiprocketAwb",
                table: "orders",
                columns: new[] { "Courier", "ShiprocketAwb" },
                unique: true,
                filter: "[Courier] = 'Shadowfax' AND [ShiprocketAwb] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_orders_ShipmentClientOrderId",
                table: "orders",
                column: "ShipmentClientOrderId",
                unique: true,
                filter: "[ShipmentClientOrderId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_Courier_ShiprocketAwb",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_ShipmentClientOrderId",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "FulfillmentReadyAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "LastShippingEventAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "LastTrackingCheckedAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ParcelBreadthCm",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ParcelHeightCm",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ParcelLengthCm",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ParcelWeightKg",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "PickedUpAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ShipmentBookedAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ShipmentClientOrderId",
                table: "orders");
        }
    }
}
