using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CrmOverhaul : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfflineDispatchNote",
                table: "orders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShippingMode",
                table: "orders",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Age",
                table: "leads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AssessmentUpdatedAt",
                table: "leads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "leads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasAssessment",
                table: "leads",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HealthConcerns",
                table: "leads",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "IsDrinking",
                table: "leads",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSmoking",
                table: "leads",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastNoAnswerAt",
                table: "leads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaritalStatus",
                table: "leads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NoAnswerAttempts",
                table: "leads",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NoAnswerStartedAt",
                table: "leads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OtherHealthConcern",
                table: "leads",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "leads",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "WorkType",
                table: "leads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProblemDuration",
                table: "leads",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousSupplements",
                table: "leads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssessmentNotes",
                table: "leads",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryConcerns",
                table: "leads",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "HealthConditions",
                table: "leads",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "bulk_campaign_recipients",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CampaignId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bulk_campaign_recipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bulk_campaigns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TemplateName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    TemplateParams = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DriveFileUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DriveFileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TotalTarget = table.Column<int>(type: "int", nullable: false),
                    SentCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bulk_campaigns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bulk_campaign_recipients_CampaignId_Status",
                table: "bulk_campaign_recipients",
                columns: new[] { "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_bulk_campaigns_CreatedAt",
                table: "bulk_campaigns",
                column: "CreatedAt");

            migrationBuilder.Sql(@"
                UPDATE l
                SET Status = 6,
                    NoAnswerAttempts = CASE WHEN NoAnswerAttempts < 1 THEN 1 ELSE NoAnswerAttempts END,
                    NoAnswerStartedAt = COALESCE(NoAnswerStartedAt, UpdatedAt),
                    LastNoAnswerAt = COALESCE(LastNoAnswerAt, UpdatedAt)
                FROM leads l
                WHERE l.Status IN (1, 3)
                  AND EXISTS (
                      SELECT 1
                      FROM follow_ups f
                      WHERE f.LeadId = l.Id
                        AND f.Status = 2
                        AND f.Outcome LIKE 'no-answer%'
                        AND f.Id = (
                            SELECT TOP 1 f2.Id
                            FROM follow_ups f2
                            WHERE f2.LeadId = l.Id AND f2.Status = 2 AND f2.Outcome IS NOT NULL
                            ORDER BY f2.CompletedAt DESC, f2.Id DESC
                        )
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bulk_campaign_recipients");

            migrationBuilder.DropTable(
                name: "bulk_campaigns");

            migrationBuilder.DropColumn(
                name: "OfflineDispatchNote",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ShippingMode",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "Age",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "AssessmentUpdatedAt",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "HasAssessment",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "HealthConcerns",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "IsDrinking",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "IsSmoking",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "LastNoAnswerAt",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "MaritalStatus",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "NoAnswerAttempts",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "NoAnswerStartedAt",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "OtherHealthConcern",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "WorkType",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "ProblemDuration",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "PreviousSupplements",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "AssessmentNotes",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "PrimaryConcerns",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "HealthConditions",
                table: "leads");
        }
    }
}
