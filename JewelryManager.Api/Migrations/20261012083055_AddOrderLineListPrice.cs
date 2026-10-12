using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderLineListPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ListPrice",
                table: "OrderLineItems",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Orders already here get the catalog's current price, so a waiting order is worded correctly.
            migrationBuilder.Sql(
                """
                UPDATE "OrderLineItems" AS l
                SET "ListPrice" = p."SitePrice"
                FROM "Products" AS p
                WHERE p."Id" = l."ProductId" AND p."SitePrice" > 0 AND l."ListPrice" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ListPrice",
                table: "OrderLineItems");
        }
    }
}
