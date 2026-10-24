using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InteractionBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    ArgumentsHash = table.Column<string>(type: "text", nullable: false),
                    ReceiptJson = table.Column<string>(type: "text", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatDispositions",
                columns: table => new
                {
                    ChatJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ForceReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatDispositions", x => x.ChatJobId);
                });

            migrationBuilder.CreateTable(
                name: "Mutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    ForceRemaining = table.Column<int>(type: "integer", nullable: false),
                    ResetGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mutes", x => x.Id);
                    table.CheckConstraint("CK_Mute_ForceRemaining", "\"ForceRemaining\" >= 0 AND \"ForceRemaining\" <= 3");
                    table.ForeignKey(
                        name: "FK_Mutes_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ForceReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    MuteSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformEventKey = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChargeGeneration = table.Column<long>(type: "bigint", nullable: false),
                    RemainingAfter = table.Column<int>(type: "integer", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompensationStatus = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForceReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ForceReceipts_Mutes_MuteSessionId",
                        column: x => x.MuteSessionId,
                        principalTable: "Mutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentOperations_ChatJobId_Ordinal",
                table: "AgentOperations",
                columns: new[] { "ChatJobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatDispositions_OperationId",
                table: "ChatDispositions",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForceReceipts_BotId_PlatformEventKey",
                table: "ForceReceipts",
                columns: new[] { "BotId", "PlatformEventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForceReceipts_ChatJobId",
                table: "ForceReceipts",
                column: "ChatJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForceReceipts_MuteSessionId",
                table: "ForceReceipts",
                column: "MuteSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Mutes_ConversationId_ExpiresAt",
                table: "Mutes",
                columns: new[] { "ConversationId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentOperations");

            migrationBuilder.DropTable(
                name: "ChatDispositions");

            migrationBuilder.DropTable(
                name: "ForceReceipts");

            migrationBuilder.DropTable(
                name: "Mutes");
        }
    }
}
