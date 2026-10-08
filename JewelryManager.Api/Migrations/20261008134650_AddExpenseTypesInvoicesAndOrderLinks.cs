using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseTypesInvoicesAndOrderLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAutomatic",
                table: "Tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TypeName",
                table: "RecurringExpenses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceContentType",
                table: "Expenses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceFileName",
                table: "Expenses",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceStoredName",
                table: "Expenses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrderId",
                table: "Expenses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TypeName",
                table: "Expenses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExpenseTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseTypes_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_OrderId",
                table: "Expenses",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTypes_BusinessId_Name",
                table: "ExpenseTypes",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_Orders_OrderId",
                table: "Expenses",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Every existing business starts with the same three expense types a new one gets.
            migrationBuilder.Sql(@"
                INSERT INTO ""ExpenseTypes"" (""Id"", ""BusinessId"", ""Name"", ""SortOrder"")
                SELECT gen_random_uuid(), b.""Id"", t.name, t.ord
                FROM ""Businesses"" b
                CROSS JOIN (VALUES ('קניית חומר', 0), ('אריזה', 1), ('שיווק', 2)) AS t(name, ord);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Orders_OrderId",
                table: "Expenses");

            migrationBuilder.DropTable(
                name: "ExpenseTypes");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_OrderId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "IsAutomatic",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "TypeName",
                table: "RecurringExpenses");

            migrationBuilder.DropColumn(
                name: "InvoiceContentType",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "InvoiceFileName",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "InvoiceStoredName",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "TypeName",
                table: "Expenses");
        }
    }
}
