using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionFixedWorkHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FixedWorkHours",
                table: "Collections",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            // Engagement rings take 3.5 work hours in total; the products already there follow (hours left
            // after the material's own hours). It can be changed in the pricing settings afterwards.
            migrationBuilder.Sql(@"
                UPDATE ""Collections"" SET ""FixedWorkHours"" = 3.5
                WHERE ""Name"" = 'טבעות אירוסין';

                UPDATE ""Products"" p
                SET ""AdditionalWorkHours"" = GREATEST(0, 3.5 - COALESCE(
                    (SELECT m.""LaborHours"" FROM ""Materials"" m
                     WHERE m.""BusinessId"" = p.""BusinessId"" AND m.""Name"" = p.""Material""), 0))
                WHERE EXISTS (
                    SELECT 1 FROM ""ProductCollections"" pc
                    JOIN ""Collections"" c ON c.""Id"" = pc.""CollectionId""
                    WHERE pc.""ProductId"" = p.""Id"" AND c.""Name"" = 'טבעות אירוסין');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FixedWorkHours",
                table: "Collections");
        }
    }
}
