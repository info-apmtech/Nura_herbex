using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertLeadOrderProductStatusesToEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop index depending on leads.Status before altering its column type.
            migrationBuilder.DropIndex(
                name: "IX_leads_Status_LastContactAt",
                table: "leads");

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
    WHERE (dc.parent_object_id = OBJECT_ID(N'products') AND c.name = N'Status')
       OR (dc.parent_object_id = OBJECT_ID(N'orders') AND c.name IN (N'PaymentMethod', N'PaymentStatus'))
       OR (dc.parent_object_id = OBJECT_ID(N'leads') AND c.name IN (N'Source', N'Status'));
    IF @dropDc IS NULL BREAK;
    EXEC sp_executesql @dropDc;
END;

UPDATE [products]
SET [Status] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(32))))) IN (N'draft', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(32))))) IN (N'archived', N'3') THEN N'3'
    ELSE N'1'
END;

UPDATE [orders]
SET [PaymentMethod] = CASE
    WHEN UPPER(LTRIM(RTRIM(CAST([PaymentMethod] AS nvarchar(16))))) IN (N'ONLINE', N'2') THEN N'2'
    ELSE N'1'
END,
    [PaymentStatus] = CASE
    WHEN UPPER(LTRIM(RTRIM(CAST([PaymentStatus] AS nvarchar(32))))) IN (N'PAID', N'2') THEN N'2'
    WHEN UPPER(LTRIM(RTRIM(CAST([PaymentStatus] AS nvarchar(32))))) IN (N'FAILED', N'3') THEN N'3'
    WHEN UPPER(LTRIM(RTRIM(CAST([PaymentStatus] AS nvarchar(32))))) IN (N'REFUNDED', N'4') THEN N'4'
    WHEN UPPER(LTRIM(RTRIM(CAST([PaymentStatus] AS nvarchar(32))))) IN (N'COD_PENDING', N'COD-PENDING', N'CODPENDING', N'5') THEN N'5'
    WHEN UPPER(LTRIM(RTRIM(CAST([PaymentStatus] AS nvarchar(32))))) IN (N'COD_COLLECTED', N'COD-COLLECTED', N'CODCOLLECTED', N'6') THEN N'6'
    WHEN UPPER(LTRIM(RTRIM(CAST([PaymentStatus] AS nvarchar(32))))) IN (N'REFUND_PENDING', N'REFUND-PENDING', N'REFUNDPENDING', N'7') THEN N'7'
    WHEN UPPER(LTRIM(RTRIM(CAST([PaymentStatus] AS nvarchar(32))))) IN (N'CANCELLED', N'CANCELED', N'8') THEN N'8'
    ELSE N'1'
END;

UPDATE [leads]
SET [Source] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'instagram', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'facebook', N'3') THEN N'3'
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'website', N'4') THEN N'4'
    WHEN LOWER(LTRIM(RTRIM(CAST([Source] AS nvarchar(32))))) IN (N'manual', N'5') THEN N'5'
    ELSE N'1'
END,
    [Status] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(32))))) IN (N'interested', N'2') THEN N'2'
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(32))))) IN (N'follow-up', N'follow_up', N'followup', N'3') THEN N'3'
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(32))))) IN (N'converted', N'4') THEN N'4'
    WHEN LOWER(LTRIM(RTRIM(CAST([Status] AS nvarchar(32))))) IN (N'not-interested', N'not_interested', N'notinterested', N'5') THEN N'5'
    ELSE N'1'
END;");

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "products",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "PaymentStatus",
                table: "orders",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "PaymentMethod",
                table: "orders",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "leads",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "Source",
                table: "leads",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.CreateIndex(
                name: "IX_leads_Status_LastContactAt",
                table: "leads",
                columns: new[] { "Status", "LastContactAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_leads_Status_LastContactAt",
                table: "leads");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "products",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "PaymentStatus",
                table: "orders",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "PaymentMethod",
                table: "orders",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "leads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Source",
                table: "leads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.Sql(@"
UPDATE [products]
SET [Status] = CASE LTRIM(RTRIM([Status]))
    WHEN N'2' THEN N'draft'
    WHEN N'3' THEN N'archived'
    ELSE N'active'
END;

UPDATE [orders]
SET [PaymentMethod] = CASE LTRIM(RTRIM([PaymentMethod]))
    WHEN N'2' THEN N'ONLINE'
    ELSE N'COD'
END,
    [PaymentStatus] = CASE LTRIM(RTRIM([PaymentStatus]))
    WHEN N'2' THEN N'PAID'
    WHEN N'3' THEN N'FAILED'
    WHEN N'4' THEN N'REFUNDED'
    WHEN N'5' THEN N'COD_PENDING'
    WHEN N'6' THEN N'COD_COLLECTED'
    WHEN N'7' THEN N'REFUND_PENDING'
    WHEN N'8' THEN N'CANCELLED'
    ELSE N'PENDING'
END;

UPDATE [leads]
SET [Source] = CASE LTRIM(RTRIM([Source]))
    WHEN N'2' THEN N'instagram'
    WHEN N'3' THEN N'facebook'
    WHEN N'4' THEN N'website'
    WHEN N'5' THEN N'manual'
    ELSE N'whatsapp'
END,
    [Status] = CASE LTRIM(RTRIM([Status]))
    WHEN N'2' THEN N'interested'
    WHEN N'3' THEN N'follow-up'
    WHEN N'4' THEN N'converted'
    WHEN N'5' THEN N'not-interested'
    ELSE N'new'
END;");

            migrationBuilder.CreateIndex(
                name: "IX_leads_Status_LastContactAt",
                table: "leads",
                columns: new[] { "Status", "LastContactAt" });
        }
    }
}
