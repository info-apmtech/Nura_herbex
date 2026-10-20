using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppMediaAndReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MediaFile",
                table: "whatsapp_messages",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaId",
                table: "whatsapp_messages",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaMime",
                table: "whatsapp_messages",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaName",
                table: "whatsapp_messages",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reaction",
                table: "whatsapp_messages",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MediaFile",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "MediaId",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "MediaMime",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "MediaName",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "Reaction",
                table: "whatsapp_messages");
        }
    }
}
