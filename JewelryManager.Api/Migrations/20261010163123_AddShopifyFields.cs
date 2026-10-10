using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddShopifyFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShopifyName",
                table: "Products",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShopifyProductId",
                table: "Products",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShopifyVariants",
                table: "Products",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Orders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalName",
                table: "Orders",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPendingApproval",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ExternalProductId",
                table: "OrderLineItems",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_BusinessId_ShopifyProductId",
                table: "Products",
                columns: new[] { "BusinessId", "ShopifyProductId" },
                unique: true,
                filter: "\"ShopifyProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessId_ExternalId",
                table: "Orders",
                columns: new[] { "BusinessId", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_BusinessId_ShopifyProductId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Orders_BusinessId_ExternalId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShopifyName",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ShopifyProductId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ShopifyVariants",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExternalName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IsPendingApproval",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExternalProductId",
                table: "OrderLineItems");
        }
    }
}
