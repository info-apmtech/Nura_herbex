using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <summary>
    /// Gives every order that still has an old "NH-…" id a 14-digit numeric number (order date yyMMdd + 8 digits) and updates
    /// everything that points at the order, in one transaction. Orders already numeric (12–15 digits) are left alone.
    /// Already-booked Shadowfax shipments keep their courier reference (ShipmentClientOrderId), so tracking is not affected.
    /// Held PayU attempts for orders that are not saved yet are untouched; they become numeric orders from now on.
    /// This cannot be reversed (the old numbers are not kept), so Down does nothing.
    /// </summary>
    [DbContext(typeof(NuraDbContext))]
    [Migration("20261104000000_RenumberOrdersTo14Digits")]
    public partial class RenumberOrdersTo14Digits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID('tempdb..#order_map') IS NOT NULL DROP TABLE #order_map;

SELECT o.Id AS OldId,
       CONVERT(varchar(6), o.CreatedAt, 12)
         + RIGHT('00000000' + CAST(10000000 + ROW_NUMBER() OVER (PARTITION BY CONVERT(varchar(6), o.CreatedAt, 12) ORDER BY o.CreatedAt, o.Id) AS varchar(12)), 8) AS NewId
INTO #order_map
FROM orders o
WHERE o.Id LIKE '%[^0-9]%' OR LEN(o.Id) NOT BETWEEN 12 AND 15;

-- Keep the courier reference of Shadowfax shipments that were booked under the old number.
UPDATE o SET ShipmentClientOrderId = o.Id
FROM orders o JOIN #order_map m ON m.OldId = o.Id
WHERE o.ShipmentClientOrderId IS NULL AND o.Courier = 'Shadowfax' AND o.ShiprocketAwb IS NOT NULL;

UPDATE t SET OrderId = m.NewId FROM payments t JOIN #order_map m ON m.OldId = t.OrderId;
UPDATE t SET OrderId = m.NewId FROM payment_attempts t JOIN #order_map m ON m.OldId = t.OrderId;
UPDATE t SET OrderId = m.NewId FROM whatsapp_messages t JOIN #order_map m ON m.OldId = t.OrderId;
UPDATE t SET OrderId = m.NewId FROM follow_ups t JOIN #order_map m ON m.OldId = t.OrderId;
UPDATE t SET OrderId = m.NewId FROM journey_enrollments t JOIN #order_map m ON m.OldId = t.OrderId;
UPDATE t SET OrderId = m.NewId FROM work_tasks t JOIN #order_map m ON m.OldId = t.OrderId;

UPDATE o SET Id = m.NewId FROM orders o JOIN #order_map m ON m.OldId = o.Id;

DROP TABLE #order_map;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Old numbers are not stored, so this cannot be undone.
        }
    }
}
