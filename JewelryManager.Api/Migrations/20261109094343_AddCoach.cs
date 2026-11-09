using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JewelryManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCoach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeferCount",
                table: "Tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DueDate",
                table: "Tasks",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentTaskId",
                table: "Tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ParkedAt",
                table: "Tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SnoozedUntil",
                table: "Tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CoachEnabled",
                table: "AgentUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CoachUnanswered",
                table: "AgentUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "FewerMessages",
                table: "AgentUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "IntroStep",
                table: "AgentUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCoachAt",
                table: "AgentUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastOutboundAt",
                table: "AgentUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUserMessageAt",
                table: "AgentUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestDays",
                table: "AgentUsers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyReview",
                table: "AgentUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "AgentMessages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "chat");

            migrationBuilder.CreateTable(
                name: "CoachFocuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Area = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FirstStep = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachFocuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoachFocuses_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CoachItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMentionedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MentionCount = table.Column<int>(type: "integer", nullable: false),
                    SnoozedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoachItems_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CoachOccasions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachOccasions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoachOccasions_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetalMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaterialName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Metal = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Purity = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    LastSettingsPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    BaselineSpotIlsPerGram = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ImpliedPremium = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    BaselineAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstCrossedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AlertedOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetalMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetalMappings_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetalPriceReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Metal = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Source = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    UsdPerOunce = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UsdIls = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    IlsPerGram = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Accepted = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetalPriceReadings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringObligations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastCreatedMonth = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringObligations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringObligations_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OldValue = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskChanges_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ParentTaskId",
                table: "Tasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_CoachFocuses_BusinessId",
                table: "CoachFocuses",
                column: "BusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachItems_BusinessId_Key",
                table: "CoachItems",
                columns: new[] { "BusinessId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachItems_BusinessId_State",
                table: "CoachItems",
                columns: new[] { "BusinessId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachOccasions_BusinessId_Code",
                table: "CoachOccasions",
                columns: new[] { "BusinessId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetalMappings_BusinessId_MaterialName",
                table: "MetalMappings",
                columns: new[] { "BusinessId", "MaterialName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetalPriceReadings_Metal_At",
                table: "MetalPriceReadings",
                columns: new[] { "Metal", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringObligations_BusinessId",
                table: "RecurringObligations",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskChanges_BusinessId_TaskId_At",
                table: "TaskChanges",
                columns: new[] { "BusinessId", "TaskId", "At" });

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Tasks_ParentTaskId",
                table: "Tasks",
                column: "ParentTaskId",
                principalTable: "Tasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Tasks_ParentTaskId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "CoachFocuses");

            migrationBuilder.DropTable(
                name: "CoachItems");

            migrationBuilder.DropTable(
                name: "CoachOccasions");

            migrationBuilder.DropTable(
                name: "MetalMappings");

            migrationBuilder.DropTable(
                name: "MetalPriceReadings");

            migrationBuilder.DropTable(
                name: "RecurringObligations");

            migrationBuilder.DropTable(
                name: "TaskChanges");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_ParentTaskId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DeferCount",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ParkedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "SnoozedUntil",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CoachEnabled",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "CoachUnanswered",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "FewerMessages",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "IntroStep",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "LastCoachAt",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "LastOutboundAt",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "LastUserMessageAt",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "RestDays",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "WeeklyReview",
                table: "AgentUsers");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "AgentMessages");
        }
    }
}
