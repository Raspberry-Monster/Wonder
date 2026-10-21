using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AutonomousMemoryMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "Memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FactKeyHash",
                table: "Memories",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastConfirmedAt",
                table: "Memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Memories",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesMemoryId",
                table: "Memories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMemoryArchive",
                table: "LogicalUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMemoryMerge",
                table: "LogicalUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMemoryUpdate",
                table: "LogicalUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RetainMemoryHistory",
                table: "LogicalUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "MemoryCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeOwnerId = table.Column<string>(type: "text", nullable: false),
                    FactKey = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    SourceReflectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevocationEpoch = table.Column<long>(type: "bigint", nullable: false),
                    CandidateHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: true),
                    TargetMemoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultMemoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryCandidates_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_SupersedesMemoryId",
                table: "Memories",
                column: "SupersedesMemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCandidates_BotId",
                table: "MemoryCandidates",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCandidates_SourceReflectionId_CandidateHash",
                table: "MemoryCandidates",
                columns: new[] { "SourceReflectionId", "CandidateHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCandidates_SubjectId_Status",
                table: "MemoryCandidates",
                columns: new[] { "SubjectId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryCandidates");

            migrationBuilder.DropIndex(
                name: "IX_Memories_SupersedesMemoryId",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "FactKeyHash",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "LastConfirmedAt",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "SupersedesMemoryId",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "AllowMemoryArchive",
                table: "LogicalUsers");

            migrationBuilder.DropColumn(
                name: "AllowMemoryMerge",
                table: "LogicalUsers");

            migrationBuilder.DropColumn(
                name: "AllowMemoryUpdate",
                table: "LogicalUsers");

            migrationBuilder.DropColumn(
                name: "RetainMemoryHistory",
                table: "LogicalUsers");
        }
    }
}
