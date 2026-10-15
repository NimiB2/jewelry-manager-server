using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseTypeGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "ExpenseTypes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "ExpenseTypes");
        }
    }
}
