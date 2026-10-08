using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseSuppliersList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpenseSuppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseSuppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseSuppliers_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuppliers_BusinessId_Name",
                table: "ExpenseSuppliers",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            // Suppliers already typed into expenses start the list, the most used first.
            migrationBuilder.Sql(@"
                INSERT INTO ""ExpenseSuppliers"" (""Id"", ""BusinessId"", ""Name"", ""SortOrder"")
                SELECT gen_random_uuid(), x.""BusinessId"", x.""Supplier"",
                       (ROW_NUMBER() OVER (PARTITION BY x.""BusinessId"" ORDER BY x.cnt DESC, x.""Supplier""))::int - 1
                FROM (SELECT ""BusinessId"", ""Supplier"", COUNT(*) AS cnt FROM ""Expenses""
                      WHERE ""Supplier"" IS NOT NULL GROUP BY ""BusinessId"", ""Supplier"") x;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseSuppliers");
        }
    }
}
