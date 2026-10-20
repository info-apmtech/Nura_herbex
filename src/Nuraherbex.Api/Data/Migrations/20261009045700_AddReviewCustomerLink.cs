using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nuraherbex.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCustomerLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerId",
                table: "reviews",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_reviews_CustomerId",
                table: "reviews",
                column: "CustomerId");

            migrationBuilder.AddForeignKey(
                name: "FK_reviews_customers_CustomerId",
                table: "reviews",
                column: "CustomerId",
                principalTable: "customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql("""
                ;WITH UniqueMatches AS
                (
                    SELECT r.[Id], MIN(c.[Id]) AS [CustomerId]
                    FROM [reviews] AS r
                    INNER JOIN [customers] AS c
                        ON LOWER(LTRIM(RTRIM(c.[FullName]))) = LOWER(LTRIM(RTRIM(r.[Name])))
                    WHERE NULLIF(LTRIM(RTRIM(r.[Name])), N'') IS NOT NULL
                    GROUP BY r.[Id]
                    HAVING COUNT_BIG(*) = 1
                )
                UPDATE r
                SET [CustomerId] = m.[CustomerId]
                FROM [reviews] AS r
                INNER JOIN UniqueMatches AS m ON m.[Id] = r.[Id]
                WHERE r.[CustomerId] IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_reviews_customers_CustomerId",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_CustomerId",
                table: "reviews");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "reviews");
        }
    }
}
