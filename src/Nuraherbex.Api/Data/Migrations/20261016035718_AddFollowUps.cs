using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "follow_ups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AssignedToEmployeeId = table.Column<long>(type: "bigint", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuggestedMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RemindAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DueNotifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EscalatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    EnrollmentId = table.Column<long>(type: "bigint", nullable: true),
                    StepKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_follow_ups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "journey_enrollments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AnchorAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StoppedReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journey_enrollments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Link = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_follow_ups_AssignedToEmployeeId_Status_DueAt",
                table: "follow_ups",
                columns: new[] { "AssignedToEmployeeId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_follow_ups_LeadId",
                table: "follow_ups",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_follow_ups_OrderId_StepKey",
                table: "follow_ups",
                columns: new[] { "OrderId", "StepKey" },
                unique: true,
                filter: "[OrderId] IS NOT NULL AND [StepKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journey_enrollments_LeadId",
                table: "journey_enrollments",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_journey_enrollments_OrderId",
                table: "journey_enrollments",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_EmployeeId_ReadAt_CreatedAt",
                table: "notifications",
                columns: new[] { "EmployeeId", "ReadAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "follow_ups");

            migrationBuilder.DropTable(
                name: "journey_enrollments");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
