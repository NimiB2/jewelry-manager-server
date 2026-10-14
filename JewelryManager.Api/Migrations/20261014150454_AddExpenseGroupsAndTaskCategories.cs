using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseGroupsAndTaskCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Tasks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "RecurringExpenses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "Expenses",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExpenseGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseGroups_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskCategories_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseGroups_BusinessId_Name",
                table: "ExpenseGroups",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskCategories_BusinessId_Name",
                table: "TaskCategories",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            // Every existing business starts with the default lists, which she can then edit in her settings.
            migrationBuilder.Sql(@"
                INSERT INTO ""ExpenseGroups"" (""Id"", ""BusinessId"", ""Name"", ""SortOrder"")
                SELECT gen_random_uuid(), b.""Id"", g.name, g.ord
                FROM ""Businesses"" b
                CROSS JOIN (VALUES ('הוצאות הזמנות', 0), ('הוצאות עסק', 1), ('הוצאות בית', 2), ('אחר', 3)) AS g(name, ord);

                INSERT INTO ""TaskCategories"" (""Id"", ""BusinessId"", ""Name"", ""SortOrder"")
                SELECT gen_random_uuid(), b.""Id"", c.name, c.ord
                FROM ""Businesses"" b
                CROSS JOIN (VALUES ('שיווק', 0), ('חומרים', 1), ('עסק', 2)) AS c(name, ord);

                UPDATE ""Expenses"" SET ""GroupName"" = 'הוצאות הזמנות' WHERE ""OrderId"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseGroups");

            migrationBuilder.DropTable(
                name: "TaskCategories");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "RecurringExpenses");

            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "Expenses");
        }
    }
}
