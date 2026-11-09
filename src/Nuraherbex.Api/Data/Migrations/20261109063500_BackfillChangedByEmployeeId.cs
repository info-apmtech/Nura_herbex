using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <summary>
    /// Backfills employee-authored <c>ChangedBy</c> values in <c>lead_assignment_history</c> and
    /// <c>lead_status_history</c> from employee email or unique employee name to the stringified employee <c>Id</c>.
    /// Automatic markers (<c>auto</c>, <c>system</c>, <c>journey</c>), already-numeric ids, and configured
    /// super-admin emails without an <c>employees</c> row are left untouched.
    /// </summary>
    [DbContext(typeof(NuraDbContext))]
    [Migration("20261109063500_BackfillChangedByEmployeeId")]
    public partial class BackfillChangedByEmployeeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE h
SET h.ChangedBy = CAST(e.Id AS nvarchar(200))
FROM lead_assignment_history h
JOIN employees e ON LOWER(LTRIM(RTRIM(h.ChangedBy))) = LOWER(LTRIM(RTRIM(e.Email)))
WHERE LOWER(LTRIM(RTRIM(h.ChangedBy))) NOT IN ('auto', 'system', 'journey')
  AND TRY_CAST(h.ChangedBy AS bigint) IS NULL;

UPDATE h
SET h.ChangedBy = CAST(e.Id AS nvarchar(200))
FROM lead_assignment_history h
JOIN employees e ON LOWER(LTRIM(RTRIM(h.ChangedBy))) = LOWER(LTRIM(RTRIM(e.Name)))
WHERE LOWER(LTRIM(RTRIM(h.ChangedBy))) NOT IN ('auto', 'system', 'journey')
  AND TRY_CAST(h.ChangedBy AS bigint) IS NULL
  AND (SELECT COUNT(*) FROM employees e2 WHERE LOWER(LTRIM(RTRIM(e2.Name))) = LOWER(LTRIM(RTRIM(e.Name)))) = 1;

UPDATE h
SET h.ChangedBy = CAST(e.Id AS nvarchar(200))
FROM lead_status_history h
JOIN employees e ON LOWER(LTRIM(RTRIM(h.ChangedBy))) = LOWER(LTRIM(RTRIM(e.Email)))
WHERE LOWER(LTRIM(RTRIM(h.ChangedBy))) NOT IN ('auto', 'system', 'journey')
  AND TRY_CAST(h.ChangedBy AS bigint) IS NULL;

UPDATE h
SET h.ChangedBy = CAST(e.Id AS nvarchar(200))
FROM lead_status_history h
JOIN employees e ON LOWER(LTRIM(RTRIM(h.ChangedBy))) = LOWER(LTRIM(RTRIM(e.Name)))
WHERE LOWER(LTRIM(RTRIM(h.ChangedBy))) NOT IN ('auto', 'system', 'journey')
  AND TRY_CAST(h.ChangedBy AS bigint) IS NULL
  AND (SELECT COUNT(*) FROM employees e2 WHERE LOWER(LTRIM(RTRIM(e2.Name))) = LOWER(LTRIM(RTRIM(e.Name)))) = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Historical name vs email representation is not retained, so this backfill cannot be undone.
        }
    }
}

