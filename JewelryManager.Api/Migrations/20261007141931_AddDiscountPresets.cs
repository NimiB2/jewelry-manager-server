using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscountPresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscountPresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountPresets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscountPresets_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscountPresets_BusinessId_Percent",
                table: "DiscountPresets",
                columns: new[] { "BusinessId", "Percent" },
                unique: true);

            // Existing businesses start with the default quick discounts.
            migrationBuilder.Sql("""
                INSERT INTO "DiscountPresets" ("Id", "BusinessId", "Percent", "SortOrder")
                SELECT gen_random_uuid(), s."BusinessId", v.percent, v.ord
                FROM "Settings" s
                CROSS JOIN (VALUES (5, 0), (10, 1), (15, 2)) AS v(percent, ord);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscountPresets");
        }
    }
}
