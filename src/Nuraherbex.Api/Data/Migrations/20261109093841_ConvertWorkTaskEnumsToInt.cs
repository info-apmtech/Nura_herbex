using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertWorkTaskEnumsToInt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop index depending on work_tasks.Status before altering its column type.
            migrationBuilder.DropIndex(
                name: "IX_work_tasks_AssignedToEmployeeId_Status_DueAt",
                table: "work_tasks");

            // Drop any manual default constraints before altering column types, and normalize existing
            // string codes (plus already-numeric values) to their integer enum values while still nvarchar.
            migrationBuilder.Sql(@"
DECLARE @dropDc nvarchar(max);
WHILE 1 = 1
BEGIN
    SET @dropDc = NULL;
    SELECT TOP (1) @dropDc = N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
    FROM sys.default_constraints dc
    JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    JOIN sys.tables t ON t.object_id = dc.parent_object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE dc.parent_object_id = OBJECT_ID(N'work_tasks')
      AND c.name IN (N'Status', N'Priority', N'Repeat');
    IF @dropDc IS NULL BREAK;
    EXEC sp_executesql @dropDc;
END;

UPDATE [work_tasks]
SET [Status] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(16))))) IN (N'in_progress', N'in-progress', N'in progress', N'inprogress', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(16))))) IN (N'blocked', N'3') THEN N'3'
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(16))))) IN (N'done', N'4') THEN N'4'
    ELSE N'1'
END,
    [Priority] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Priority] AS nvarchar(16))))) IN (N'low', N'1') THEN N'1'
    WHEN LOWER(LTRIM(RTRIM(CAST([Priority] AS nvarchar(16))))) IN (N'high', N'3') THEN N'3'
    WHEN LOWER(LTRIM(RTRIM(CAST([Priority] AS nvarchar(16))))) IN (N'urgent', N'4') THEN N'4'
    ELSE N'2'
END,
    [Repeat] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Repeat] AS nvarchar(16))))) IN (N'daily', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([Repeat] AS nvarchar(16))))) IN (N'weekly', N'3') THEN N'3'
    WHEN LOWER(LTRIM(RTRIM(CAST([Repeat] AS nvarchar(16))))) IN (N'monthly', N'4') THEN N'4'
    ELSE N'1'
END;");

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "work_tasks",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<int>(
                name: "Repeat",
                table: "work_tasks",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "work_tasks",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.CreateIndex(
                name: "IX_work_tasks_AssignedToEmployeeId_Status_DueAt",
                table: "work_tasks",
                columns: new[] { "AssignedToEmployeeId", "Status", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_work_tasks_AssignedToEmployeeId_Status_DueAt",
                table: "work_tasks");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "work_tasks",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Repeat",
                table: "work_tasks",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Priority",
                table: "work_tasks",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.Sql(@"
UPDATE [work_tasks]
SET [Status] = CASE LTRIM(RTRIM([Status]))
    WHEN N'2' THEN N'in_progress'
    WHEN N'3' THEN N'blocked'
    WHEN N'4' THEN N'done'
    ELSE N'todo'
END,
    [Priority] = CASE LTRIM(RTRIM([Priority]))
    WHEN N'1' THEN N'low'
    WHEN N'3' THEN N'high'
    WHEN N'4' THEN N'urgent'
    ELSE N'normal'
END,
    [Repeat] = CASE LTRIM(RTRIM([Repeat]))
    WHEN N'2' THEN N'daily'
    WHEN N'3' THEN N'weekly'
    WHEN N'4' THEN N'monthly'
    ELSE N'none'
END;");

            migrationBuilder.CreateIndex(
                name: "IX_work_tasks_AssignedToEmployeeId_Status_DueAt",
                table: "work_tasks",
                columns: new[] { "AssignedToEmployeeId", "Status", "DueAt" });
        }
    }
}
