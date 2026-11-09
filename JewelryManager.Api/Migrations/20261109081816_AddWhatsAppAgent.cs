using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nickname = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    FullName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentAliases_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Instruction = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentPreferences_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentProcessedMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProcessedMessages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "AgentUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Gender = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeveloper = table.Column<bool>(type: "boolean", nullable: false),
                    MorningSummary = table.Column<bool>(type: "boolean", nullable: false),
                    StoreOrderAlerts = table.Column<bool>(type: "boolean", nullable: false),
                    StuckOrderAlerts = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentUsers_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentUsers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tool = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ArgsJson = table.Column<string>(type: "text", nullable: false),
                    Ok = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UndoJson = table.Column<string>(type: "text", nullable: true),
                    UndoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentAuditEntries_AgentUsers_AgentUserId",
                        column: x => x.AgentUserId,
                        principalTable: "AgentUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentMessages_AgentUsers_AgentUserId",
                        column: x => x.AgentUserId,
                        principalTable: "AgentUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentPendingActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ArgsJson = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPendingActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentPendingActions_AgentUsers_AgentUserId",
                        column: x => x.AgentUserId,
                        principalTable: "AgentUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentProactiveLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProactiveLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentProactiveLog_AgentUsers_AgentUserId",
                        column: x => x.AgentUserId,
                        principalTable: "AgentUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Model = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    CacheReadTokens = table.Column<int>(type: "integer", nullable: false),
                    CacheWriteTokens = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentUsages_AgentUsers_AgentUserId",
                        column: x => x.AgentUserId,
                        principalTable: "AgentUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAliases_BusinessId_Nickname",
                table: "AgentAliases",
                columns: new[] { "BusinessId", "Nickname" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentAuditEntries_AgentUserId_CreatedAt",
                table: "AgentAuditEntries",
                columns: new[] { "AgentUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMessages_AgentUserId_CreatedAt",
                table: "AgentMessages",
                columns: new[] { "AgentUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPendingActions_AgentUserId_CreatedAt",
                table: "AgentPendingActions",
                columns: new[] { "AgentUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPreferences_BusinessId_AgentUserId_Key",
                table: "AgentPreferences",
                columns: new[] { "BusinessId", "AgentUserId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentProactiveLog_AgentUserId_Kind_Key",
                table: "AgentProactiveLog",
                columns: new[] { "AgentUserId", "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentProcessedMessages_ReceivedAt",
                table: "AgentProcessedMessages",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AgentUsages_AgentUserId_CreatedAt",
                table: "AgentUsages",
                columns: new[] { "AgentUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentUsages_BusinessId_CreatedAt",
                table: "AgentUsages",
                columns: new[] { "BusinessId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentUsers_BusinessId",
                table: "AgentUsers",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentUsers_Phone",
                table: "AgentUsers",
                column: "Phone",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentUsers_UserId",
                table: "AgentUsers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentAliases");

            migrationBuilder.DropTable(
                name: "AgentAuditEntries");

            migrationBuilder.DropTable(
                name: "AgentMessages");

            migrationBuilder.DropTable(
                name: "AgentPendingActions");

            migrationBuilder.DropTable(
                name: "AgentPreferences");

            migrationBuilder.DropTable(
                name: "AgentProactiveLog");

            migrationBuilder.DropTable(
                name: "AgentProcessedMessages");

            migrationBuilder.DropTable(
                name: "AgentUsages");

            migrationBuilder.DropTable(
                name: "AgentUsers");
        }
    }
}
