using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskStartDateAndChecklistAudit : Migration
    {
        // Each column is added only when it is missing, so the migration also succeeds on a database where an earlier draft of it was applied.
        private static string AddIfMissing(string table, string column, string type) =>
            $"IF COL_LENGTH('{table}', '{column}') IS NULL ALTER TABLE [{table}] ADD [{column}] {type};";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AddIfMissing("work_tasks", "StartAt", "datetime2 NULL"));
            migrationBuilder.Sql(AddIfMissing("work_tasks", "StartSoonNotifiedAt", "datetime2 NULL"));
            migrationBuilder.Sql(AddIfMissing("work_tasks", "StartedNotifiedAt", "datetime2 NULL"));
            migrationBuilder.Sql(AddIfMissing("task_checklist", "AddedAt", "datetime2 NOT NULL DEFAULT '0001-01-01T00:00:00'"));
            migrationBuilder.Sql(AddIfMissing("task_checklist", "AddedByName", "nvarchar(max) NULL"));
            migrationBuilder.Sql(AddIfMissing("task_checklist", "DoneAt", "datetime2 NULL"));
            migrationBuilder.Sql(AddIfMissing("task_checklist", "DoneByName", "nvarchar(max) NULL"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "StartAt", table: "work_tasks");
            migrationBuilder.DropColumn(name: "StartSoonNotifiedAt", table: "work_tasks");
            migrationBuilder.DropColumn(name: "StartedNotifiedAt", table: "work_tasks");
            migrationBuilder.DropColumn(name: "AddedAt", table: "task_checklist");
            migrationBuilder.DropColumn(name: "AddedByName", table: "task_checklist");
            migrationBuilder.DropColumn(name: "DoneAt", table: "task_checklist");
            migrationBuilder.DropColumn(name: "DoneByName", table: "task_checklist");
        }
    }
}
