using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertEmployeeRoleAndCouponDiscountTypeToEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop any manual default constraints before altering types, and convert existing string
            // codes ('admin'/'sales', 'PERCENTAGE'/'FIXED') to their integer enum values while still nvarchar.
            migrationBuilder.Sql(@"
DECLARE @empDc nvarchar(max);
SELECT @empDc = N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
FROM sys.default_constraints dc
JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
JOIN sys.tables t ON t.object_id = dc.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE dc.parent_object_id = OBJECT_ID(N'employees') AND c.name = N'Role';
IF @empDc IS NOT NULL EXEC sp_executesql @empDc;

UPDATE [employees]
SET [Role] = CASE
    WHEN LOWER(LTRIM(RTRIM(CAST([Role] AS nvarchar(32))))) IN (N'admin', N'1') THEN N'1'
    ELSE N'2'
END;

DECLARE @couponDc nvarchar(max);
SELECT @couponDc = N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
FROM sys.default_constraints dc
JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
JOIN sys.tables t ON t.object_id = dc.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE dc.parent_object_id = OBJECT_ID(N'coupons') AND c.name = N'DiscountType';
IF @couponDc IS NOT NULL EXEC sp_executesql @couponDc;

UPDATE [coupons]
SET [DiscountType] = CASE
    WHEN UPPER(LTRIM(RTRIM(CAST([DiscountType] AS nvarchar(32))))) IN (N'FIXED', N'2') THEN N'2'
    ELSE N'1'
END;");

            migrationBuilder.AlterColumn<int>(
                name: "Role",
                table: "employees",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<int>(
                name: "DiscountType",
                table: "coupons",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "employees",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "DiscountType",
                table: "coupons",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.Sql(@"
UPDATE [employees]
SET [Role] = CASE
    WHEN LTRIM(RTRIM([Role])) = N'1' THEN N'admin'
    ELSE N'sales'
END;

UPDATE [coupons]
SET [DiscountType] = CASE
    WHEN LTRIM(RTRIM([DiscountType])) = N'2' THEN N'FIXED'
    ELSE N'PERCENTAGE'
END;");
        }
    }
}
