using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskStartDateAndChecklistAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StartAt",
                table: "work_tasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartSoonNotifiedAt",
                table: "work_tasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedNotifiedAt",
                table: "work_tasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AddedAt",
                table: "task_checklist",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "AddedByName",
                table: "task_checklist",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DoneAt",
                table: "task_checklist",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DoneByName",
                table: "task_checklist",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartAt",
                table: "work_tasks");

            migrationBuilder.DropColumn(
                name: "StartSoonNotifiedAt",
                table: "work_tasks");

            migrationBuilder.DropColumn(
                name: "StartedNotifiedAt",
                table: "work_tasks");

            migrationBuilder.DropColumn(
                name: "AddedAt",
                table: "task_checklist");

            migrationBuilder.DropColumn(
                name: "AddedByName",
                table: "task_checklist");

            migrationBuilder.DropColumn(
                name: "DoneAt",
                table: "task_checklist");

            migrationBuilder.DropColumn(
                name: "DoneByName",
                table: "task_checklist");
        }
    }
}
