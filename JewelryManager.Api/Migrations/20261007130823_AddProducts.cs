using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "FeeItems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProductAdditionTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AllowsCustomName = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductAdditionTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductAdditionTypes_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Material = table.Column<string>(type: "text", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AdditionalWorkHours = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SitePrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Products_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductAdditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeName = table.Column<string>(type: "text", nullable: false),
                    CustomName = table.Column<string>(type: "text", nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductAdditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductAdditions_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductAdditions_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductCollections",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCollections", x => new { x.ProductId, x.CollectionId });
                    table.ForeignKey(
                        name: "FK_ProductCollections_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductCollections_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductCollections_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeeItems_BusinessId_Key",
                table: "FeeItems",
                columns: new[] { "BusinessId", "Key" },
                unique: true,
                filter: "\"Key\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProductAdditions_BusinessId",
                table: "ProductAdditions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductAdditions_ProductId",
                table: "ProductAdditions",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductAdditionTypes_BusinessId_Name",
                table: "ProductAdditionTypes",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCollections_BusinessId",
                table: "ProductCollections",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCollections_CollectionId",
                table: "ProductCollections",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_BusinessId",
                table: "Products",
                column: "BusinessId");

            // Existing data: tag the three fees the pricing formula reads, and give every
            // business the default product addition types.
            migrationBuilder.Sql("""
                UPDATE "FeeItems"
                SET "Key" = CASE "Name"
                    WHEN 'עמלת סליקה' THEN 'cardFee'
                    WHEN 'מע"מ' THEN 'vat'
                    WHEN 'עמלת עלויות קבועות' THEN 'fixedExpenses'
                END
                WHERE "IsPermanent" AND "Name" IN ('עמלת סליקה', 'מע"מ', 'עמלת עלויות קבועות');

                INSERT INTO "ProductAdditionTypes" ("Id", "BusinessId", "Name", "AllowsCustomName", "SortOrder")
                SELECT gen_random_uuid(), s."BusinessId", v.name, v.custom, v.ord
                FROM "Settings" s
                CROSS JOIN (VALUES ('אבן', false, 0), ('שיבוץ', false, 1), ('ציפוי', false, 2),
                                   ('תוספת עגילים', false, 3), ('תוספת שרשרת', false, 4), ('אחר', true, 5))
                     AS v(name, custom, ord);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductAdditions");

            migrationBuilder.DropTable(
                name: "ProductAdditionTypes");

            migrationBuilder.DropTable(
                name: "ProductCollections");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropIndex(
                name: "IX_FeeItems_BusinessId_Key",
                table: "FeeItems");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "FeeItems");
        }
    }
}
