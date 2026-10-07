using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderLineNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "OrderLineItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Note",
                table: "OrderLineItems");
        }
    }
}
