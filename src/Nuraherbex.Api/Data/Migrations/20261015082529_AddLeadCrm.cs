using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadCrm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LeadId",
                table: "whatsapp_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    LastInboundAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastOutboundAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UnreadCount = table.Column<int>(type: "int", nullable: false),
                    ServiceWindowExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lead_source_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReferralJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_source_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lead_status_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_status_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "leads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceDetail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConsentStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConsentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsentSource = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FirstMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastContactAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConvertedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leads", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_messages_LeadId",
                table: "whatsapp_messages",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_LeadId",
                table: "conversations",
                column: "LeadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lead_source_events_LeadId",
                table: "lead_source_events",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_status_history_LeadId",
                table: "lead_status_history",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_leads_Phone",
                table: "leads",
                column: "Phone",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leads_Status_LastContactAt",
                table: "leads",
                columns: new[] { "Status", "LastContactAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "lead_source_events");

            migrationBuilder.DropTable(
                name: "lead_status_history");

            migrationBuilder.DropTable(
                name: "leads");

            migrationBuilder.DropIndex(
                name: "IX_whatsapp_messages_LeadId",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "LeadId",
                table: "whatsapp_messages");
        }
    }
}
