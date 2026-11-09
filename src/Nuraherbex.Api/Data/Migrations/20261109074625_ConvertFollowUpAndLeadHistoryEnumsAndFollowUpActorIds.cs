using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertFollowUpAndLeadHistoryEnumsAndFollowUpActorIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop index depending on follow_ups.Status before altering its column type.
            migrationBuilder.DropIndex(
                name: "IX_follow_ups_AssignedToEmployeeId_Status_DueAt",
                table: "follow_ups");

            // Drop any manual default constraints before altering column types, convert existing string
            // status/source codes to integer enum values while still nvarchar, and backfill follow_ups
            // employee-authored CreatedBy / CompletedBy values to numeric employee IDs.
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
    WHERE (dc.parent_object_id = OBJECT_ID(N'follow_ups') AND c.name IN (N'Status', N'Source'))
       OR (dc.parent_object_id = OBJECT_ID(N'lead_source_events') AND c.name = N'Source')
       OR (dc.parent_object_id = OBJECT_ID(N'lead_status_history') AND c.name IN (N'FromStatus', N'ToStatus'));
    IF @dropDc IS NULL BREAK;
    EXEC sp_executesql @dropDc;
END;

UPDATE [follow_ups]
SET [Status] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(32))))) IN (N'done', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(32))))) IN (N'skipped', N'3') THEN N'3'
    ELSE N'1'
END,
    [Source] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'journey', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'system', N'3') THEN N'3'
    ELSE N'1'
END;

UPDATE f
SET f.CreatedBy = CAST(e.Id AS nvarchar(200))
FROM follow_ups f
JOIN employees e ON LOWER(LTRIM(RTRIM(f.CreatedBy))) = LOWER(LTRIM(RTRIM(e.Email)))
WHERE f.CreatedBy IS NOT NULL
  AND LTRIM(RTRIM(f.CreatedBy)) <> N''
  AND LOWER(LTRIM(RTRIM(f.CreatedBy))) NOT IN (N'auto', N'system', N'journey')
  AND TRY_CAST(f.CreatedBy AS bigint) IS NULL;

UPDATE f
SET f.CreatedBy = CAST(e.Id AS nvarchar(200))
FROM follow_ups f
JOIN employees e ON LOWER(LTRIM(RTRIM(f.CreatedBy))) = LOWER(LTRIM(RTRIM(e.Name)))
WHERE f.CreatedBy IS NOT NULL
  AND LTRIM(RTRIM(f.CreatedBy)) <> N''
  AND LOWER(LTRIM(RTRIM(f.CreatedBy))) NOT IN (N'auto', N'system', N'journey')
  AND TRY_CAST(f.CreatedBy AS bigint) IS NULL
  AND (SELECT COUNT(*) FROM employees e2 WHERE LOWER(LTRIM(RTRIM(e2.Name))) = LOWER(LTRIM(RTRIM(e.Name)))) = 1;

UPDATE f
SET f.CompletedBy = CAST(e.Id AS nvarchar(200))
FROM follow_ups f
JOIN employees e ON LOWER(LTRIM(RTRIM(f.CompletedBy))) = LOWER(LTRIM(RTRIM(e.Email)))
WHERE f.CompletedBy IS NOT NULL
  AND LTRIM(RTRIM(f.CompletedBy)) <> N''
  AND LOWER(LTRIM(RTRIM(f.CompletedBy))) NOT IN (N'auto', N'system', N'journey')
  AND TRY_CAST(f.CompletedBy AS bigint) IS NULL;

UPDATE f
SET f.CompletedBy = CAST(e.Id AS nvarchar(200))
FROM follow_ups f
JOIN employees e ON LOWER(LTRIM(RTRIM(f.CompletedBy))) = LOWER(LTRIM(RTRIM(e.Name)))
WHERE f.CompletedBy IS NOT NULL
  AND LTRIM(RTRIM(f.CompletedBy)) <> N''
  AND LOWER(LTRIM(RTRIM(f.CompletedBy))) NOT IN (N'auto', N'system', N'journey')
  AND TRY_CAST(f.CompletedBy AS bigint) IS NULL
  AND (SELECT COUNT(*) FROM employees e2 WHERE LOWER(LTRIM(RTRIM(e2.Name))) = LOWER(LTRIM(RTRIM(e.Name)))) = 1;

UPDATE [lead_source_events]
SET [Source] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'instagram', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'facebook', N'3') THEN N'3'
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'website', N'4') THEN N'4'
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'manual', N'5') THEN N'5'
    ELSE N'1'
END;

UPDATE [lead_status_history]
SET [FromStatus] = CASE
    WHEN [FromStatus] IS NULL OR LTRIM(RTRIM(CAST([FromStatus] AS nvarchar(32)))) = N'' THEN NULL
    WHEN LOWER(LTRIM(RTRIM(CAST([FromStatus] AS nvarchar(32))))) IN (N'new', N'1') THEN N'1'
    WHEN LOWER(LTRIM(RTRIM(CAST([FromStatus] AS nvarchar(32))))) IN (N'interested', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([FromStatus] AS nvarchar(32))))) IN (N'follow-up', N'follow_up', N'3') THEN N'3'
    WHEN LOWER(LTRIM(RTRIM(CAST([FromStatus] AS nvarchar(32))))) IN (N'converted', N'4') THEN N'4'
    WHEN LOWER(LTRIM(RTRIM(CAST([FromStatus] AS nvarchar(32))))) IN (N'not-interested', N'not_interested', N'5') THEN N'5'
    ELSE NULL
END,
    [ToStatus] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([ToStatus] AS nvarchar(32))))) IN (N'interested', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([ToStatus] AS nvarchar(32))))) IN (N'follow-up', N'follow_up', N'3') THEN N'3'
    WHEN LOWER(LTRIM(RTRIM(CAST([ToStatus] AS nvarchar(32))))) IN (N'converted', N'4') THEN N'4'
    WHEN LOWER(LTRIM(RTRIM(CAST([ToStatus] AS nvarchar(32))))) IN (N'not-interested', N'not_interested', N'5') THEN N'5'
    ELSE N'1'
END;");

            migrationBuilder.AlterColumn<int>(
                name: "ToStatus",
                table: "lead_status_history",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "FromStatus",
                table: "lead_status_history",
                type: "int",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Source",
                table: "lead_source_events",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "follow_ups",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<int>(
                name: "Source",
                table: "follow_ups",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.CreateIndex(
                name: "IX_follow_ups_AssignedToEmployeeId_Status_DueAt",
                table: "follow_ups",
                columns: new[] { "AssignedToEmployeeId", "Status", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_follow_ups_AssignedToEmployeeId_Status_DueAt",
                table: "follow_ups");

            migrationBuilder.AlterColumn<string>(
                name: "ToStatus",
                table: "lead_status_history",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "FromStatus",
                table: "lead_status_history",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Source",
                table: "lead_source_events",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "follow_ups",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Source",
                table: "follow_ups",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.Sql(@"
UPDATE [follow_ups]
SET [Status] = CASE LTRIM(RTRIM([Status]))
    WHEN N'2' THEN N'done'
    WHEN N'3' THEN N'skipped'
    ELSE N'pending'
END,
    [Source] = CASE LTRIM(RTRIM([Source]))
    WHEN N'2' THEN N'journey'
    WHEN N'3' THEN N'system'
    ELSE N'manual'
END;

UPDATE [lead_source_events]
SET [Source] = CASE LTRIM(RTRIM([Source]))
    WHEN N'2' THEN N'instagram'
    WHEN N'3' THEN N'facebook'
    WHEN N'4' THEN N'website'
    WHEN N'5' THEN N'manual'
    ELSE N'whatsapp'
END;

UPDATE [lead_status_history]
SET [FromStatus] = CASE
    WHEN [FromStatus] IS NULL THEN NULL
    WHEN LTRIM(RTRIM([FromStatus])) = N'1' THEN N'new'
    WHEN LTRIM(RTRIM([FromStatus])) = N'2' THEN N'interested'
    WHEN LTRIM(RTRIM([FromStatus])) = N'3' THEN N'follow-up'
    WHEN LTRIM(RTRIM([FromStatus])) = N'4' THEN N'converted'
    WHEN LTRIM(RTRIM([FromStatus])) = N'5' THEN N'not-interested'
    ELSE NULL
END,
    [ToStatus] = CASE LTRIM(RTRIM([ToStatus]))
    WHEN N'2' THEN N'interested'
    WHEN N'3' THEN N'follow-up'
    WHEN N'4' THEN N'converted'
    WHEN N'5' THEN N'not-interested'
    ELSE N'new'
END;");

            migrationBuilder.CreateIndex(
                name: "IX_follow_ups_AssignedToEmployeeId_Status_DueAt",
                table: "follow_ups",
                columns: new[] { "AssignedToEmployeeId", "Status", "DueAt" });
        }
    }
}
