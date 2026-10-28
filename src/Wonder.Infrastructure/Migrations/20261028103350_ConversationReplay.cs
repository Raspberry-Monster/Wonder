using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConversationReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationReplays",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FormatVersion = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    FinalMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    SummaryBaseline = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationReplays", x => x.ConversationId);
                    table.ForeignKey(
                        name: "FK_ConversationReplays_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationReplays_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReplayDependencies",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayDependencies", x => new { x.ConversationId, x.Kind, x.ResourceId, x.Version });
                    table.ForeignKey(
                        name: "FK_ReplayDependencies_ConversationReplays_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "ConversationReplays",
                        principalColumn: "ConversationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReplays_BotId",
                table: "ConversationReplays",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReplays_ExpiresAt",
                table: "ConversationReplays",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReplays_UserId",
                table: "ConversationReplays",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReplayDependencies_Kind_ResourceId",
                table: "ReplayDependencies",
                columns: new[] { "Kind", "ResourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReplayDependencies");

            migrationBuilder.DropTable(
                name: "ConversationReplays");
        }
    }
}
