using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VersionedNotesAndPrivacyLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecordVersion",
                table: "Revocations",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "ResourceId",
                table: "Revocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceKind",
                table: "Revocations",
                type: "text",
                nullable: false,
                defaultValue: "Memory");

            migrationBuilder.AddColumn<long>(
                name: "ResourceVersion",
                table: "Revocations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowNotes",
                table: "LogicalUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "NotesEpoch",
                table: "LogicalUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutorLeaseId",
                table: "AgentOperations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "NoteDependencies",
                columns: table => new
                {
                    NoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    TargetKind = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteDependencies", x => new { x.NoteId, x.Version, x.TargetKind, x.TargetId });
                });

            migrationBuilder.CreateTable(
                name: "NoteRevisions",
                columns: table => new
                {
                    NoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteRevisions", x => new { x.NoteId, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorKind = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ClaimKind = table.Column<string>(type: "text", nullable: true),
                    SourceMessageIdsJson = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PrivacyDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteDependencies_TargetKind_TargetId",
                table: "NoteDependencies",
                columns: new[] { "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_BotId_UserId_ConversationId",
                table: "Notes",
                columns: new[] { "BotId", "UserId", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteDependencies");

            migrationBuilder.DropTable(
                name: "NoteRevisions");

            migrationBuilder.DropTable(
                name: "Notes");

            migrationBuilder.DropColumn(
                name: "RecordVersion",
                table: "Revocations");

            migrationBuilder.DropColumn(
                name: "ResourceId",
                table: "Revocations");

            migrationBuilder.DropColumn(
                name: "ResourceKind",
                table: "Revocations");

            migrationBuilder.DropColumn(
                name: "ResourceVersion",
                table: "Revocations");

            migrationBuilder.DropColumn(
                name: "AllowNotes",
                table: "LogicalUsers");

            migrationBuilder.DropColumn(
                name: "NotesEpoch",
                table: "LogicalUsers");

            migrationBuilder.DropColumn(
                name: "ExecutorLeaseId",
                table: "AgentOperations");
        }
    }
}
