using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LaborHourRate",
                table: "Settings",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ProfitFloorPercent",
                table: "Settings",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "FeeItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Percent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IsPermanent = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeeItems_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Materials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PricePerGram = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    LaborHoursPerGram = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ProfitMultiplier = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Materials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Materials_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PreparationStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparationStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreparationStages_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PricingAdditionCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BasePrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingAdditionCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PricingAdditionCategories_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PricingAdditionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingAdditionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PricingAdditionItems_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PricingAdditionItems_PricingAdditionCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "PricingAdditionCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeeItems_BusinessId_Name",
                table: "FeeItems",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Materials_BusinessId_Name",
                table: "Materials",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreparationStages_BusinessId_Name",
                table: "PreparationStages",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PricingAdditionCategories_BusinessId_Name",
                table: "PricingAdditionCategories",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PricingAdditionItems_BusinessId",
                table: "PricingAdditionItems",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_PricingAdditionItems_CategoryId_Name",
                table: "PricingAdditionItems",
                columns: new[] { "CategoryId", "Name" },
                unique: true);

            // Move the existing JSON settings into the new tables BEFORE dropping the column.
            migrationBuilder.Sql("""
                UPDATE "Settings"
                SET "LaborHourRate" = COALESCE(("Data"->>'laborHourRate')::numeric, 0),
                    "ProfitFloorPercent" = COALESCE(("Data"->>'profitFloorPercent')::numeric, 0);

                INSERT INTO "Materials" ("Id", "BusinessId", "Name", "PricePerGram", "LaborHoursPerGram", "ProfitMultiplier", "SortOrder")
                SELECT gen_random_uuid(), s."BusinessId", m.key,
                       COALESCE((m.value->>'pricePerGram')::numeric, 0),
                       COALESCE((m.value->>'laborHoursPerGram')::numeric, 0),
                       COALESCE((m.value->>'profitMultiplier')::numeric, 0),
                       (row_number() OVER (PARTITION BY s."BusinessId" ORDER BY m.key))::int - 1
                FROM "Settings" s, jsonb_each(s."Data"->'materials') AS m
                WHERE jsonb_typeof(s."Data"->'materials') = 'object';

                INSERT INTO "FeeItems" ("Id", "BusinessId", "Name", "Percent", "IsPermanent", "SortOrder")
                SELECT gen_random_uuid(), s."BusinessId", f.value->>'name',
                       COALESCE((f.value->>'percent')::numeric, 0),
                       COALESCE((f.value->>'isPermanent')::boolean, false),
                       (f.ord - 1)::int
                FROM "Settings" s, jsonb_array_elements(s."Data"->'feesItems') WITH ORDINALITY AS f(value, ord)
                WHERE jsonb_typeof(s."Data"->'feesItems') = 'array';

                INSERT INTO "PricingAdditionCategories" ("Id", "BusinessId", "Name", "BasePrice", "SortOrder")
                SELECT gen_random_uuid(), s."BusinessId", c.value->>'name',
                       COALESCE((c.value->>'basePrice')::numeric, 0), (c.ord - 1)::int
                FROM "Settings" s, jsonb_array_elements(s."Data"->'pricingAdditions') WITH ORDINALITY AS c(value, ord)
                WHERE jsonb_typeof(s."Data"->'pricingAdditions') = 'array';

                INSERT INTO "PricingAdditionItems" ("Id", "BusinessId", "CategoryId", "Name", "Price", "SortOrder")
                SELECT gen_random_uuid(), s."BusinessId", pc."Id", i.value->>'name',
                       COALESCE((i.value->>'price')::numeric, 0), (i.ord - 1)::int
                FROM "Settings" s
                CROSS JOIN LATERAL jsonb_array_elements(s."Data"->'pricingAdditions') WITH ORDINALITY AS c(value, ord)
                CROSS JOIN LATERAL jsonb_array_elements(c.value->'items') WITH ORDINALITY AS i(value, ord)
                JOIN "PricingAdditionCategories" pc ON pc."BusinessId" = s."BusinessId" AND pc."Name" = c.value->>'name'
                WHERE jsonb_typeof(s."Data"->'pricingAdditions') = 'array'
                  AND jsonb_typeof(c.value->'items') = 'array';

                INSERT INTO "PreparationStages" ("Id", "BusinessId", "Name", "SortOrder")
                SELECT gen_random_uuid(), s."BusinessId", p.value, (p.ord - 1)::int
                FROM "Settings" s, jsonb_array_elements_text(s."Data"->'preparationStages') WITH ORDINALITY AS p(value, ord)
                WHERE jsonb_typeof(s."Data"->'preparationStages') = 'array';
                """);

            migrationBuilder.DropColumn(
                name: "Data",
                table: "Settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Settings\" ADD COLUMN \"Data\" jsonb NOT NULL DEFAULT '{}'::jsonb;");

            migrationBuilder.Sql("""
                UPDATE "Settings" s SET "Data" = jsonb_build_object(
                    'laborHourRate', s."LaborHourRate",
                    'profitFloorPercent', s."ProfitFloorPercent",
                    'materials', COALESCE((SELECT jsonb_object_agg(m."Name", jsonb_build_object(
                        'pricePerGram', m."PricePerGram", 'laborHoursPerGram', m."LaborHoursPerGram",
                        'profitMultiplier', m."ProfitMultiplier"))
                        FROM "Materials" m WHERE m."BusinessId" = s."BusinessId"), '{}'::jsonb),
                    'feesItems', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                        'name', f."Name", 'percent', f."Percent", 'isPermanent', f."IsPermanent") ORDER BY f."SortOrder")
                        FROM "FeeItems" f WHERE f."BusinessId" = s."BusinessId"), '[]'::jsonb),
                    'pricingAdditions', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                        'name', c."Name", 'basePrice', c."BasePrice",
                        'items', COALESCE((SELECT jsonb_agg(jsonb_build_object('name', i."Name", 'price', i."Price") ORDER BY i."SortOrder")
                            FROM "PricingAdditionItems" i WHERE i."CategoryId" = c."Id"), '[]'::jsonb)) ORDER BY c."SortOrder")
                        FROM "PricingAdditionCategories" c WHERE c."BusinessId" = s."BusinessId"), '[]'::jsonb),
                    'preparationStages', COALESCE((SELECT jsonb_agg(p."Name" ORDER BY p."SortOrder")
                        FROM "PreparationStages" p WHERE p."BusinessId" = s."BusinessId"), '[]'::jsonb));
                """);

            migrationBuilder.DropTable(
                name: "FeeItems");

            migrationBuilder.DropTable(
                name: "Materials");

            migrationBuilder.DropTable(
                name: "PreparationStages");

            migrationBuilder.DropTable(
                name: "PricingAdditionItems");

            migrationBuilder.DropTable(
                name: "PricingAdditionCategories");

            migrationBuilder.DropColumn(
                name: "LaborHourRate",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ProfitFloorPercent",
                table: "Settings");
        }
    }
}
