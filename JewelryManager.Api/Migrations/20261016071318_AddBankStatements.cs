using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBankStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CardSettlementKeywords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Keyword = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardSettlementKeywords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardSettlementKeywords_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatementFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    FileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: true),
                    TotalOut = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatementFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatementFiles_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatementMonthCloses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatementMonthCloses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatementMonthCloses_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatementLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpenseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoveredByFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatementLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatementLines_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StatementLines_Expenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StatementLines_StatementFiles_CoveredByFileId",
                        column: x => x.CoveredByFileId,
                        principalTable: "StatementFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StatementLines_StatementFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "StatementFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardSettlementKeywords_BusinessId_Keyword",
                table: "CardSettlementKeywords",
                columns: new[] { "BusinessId", "Keyword" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatementFiles_BusinessId_FileHash",
                table: "StatementFiles",
                columns: new[] { "BusinessId", "FileHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_BusinessId_Date",
                table: "StatementLines",
                columns: new[] { "BusinessId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_BusinessId_Fingerprint",
                table: "StatementLines",
                columns: new[] { "BusinessId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_CoveredByFileId",
                table: "StatementLines",
                column: "CoveredByFileId");

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_ExpenseId",
                table: "StatementLines",
                column: "ExpenseId",
                unique: true,
                filter: "\"ExpenseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_FileId",
                table: "StatementLines",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_StatementMonthCloses_BusinessId_Month",
                table: "StatementMonthCloses",
                columns: new[] { "BusinessId", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardSettlementKeywords");

            migrationBuilder.DropTable(
                name: "StatementLines");

            migrationBuilder.DropTable(
                name: "StatementMonthCloses");

            migrationBuilder.DropTable(
                name: "StatementFiles");
        }
    }
}
