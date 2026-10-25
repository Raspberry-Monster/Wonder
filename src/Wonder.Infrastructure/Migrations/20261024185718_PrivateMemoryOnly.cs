using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PrivateMemoryOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GroupPolicies_ContextSpaces_ContextSpaceId",
                table: "GroupPolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_Memories_Memories_SharedFromMemoryId",
                table: "Memories");

            migrationBuilder.DropTable(
                name: "ContextSpaces");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Revocations_Scope",
                table: "Revocations");

            migrationBuilder.DropIndex(
                name: "IX_Relationships_BotId_UserId_GroupId",
                table: "Relationships");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MemoryCandidates_Scope",
                table: "MemoryCandidates");

            migrationBuilder.DropIndex(
                name: "IX_Memories_BotId_Scope_ScopeOwnerId_Status",
                table: "Memories");

            migrationBuilder.DropIndex(
                name: "IX_Memories_SharedFromMemoryId_Scope_ScopeOwnerId",
                table: "Memories");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Memories_Scope",
                table: "Memories");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Memory_Enums",
                table: "Memories");

            migrationBuilder.DropIndex(
                name: "IX_GroupPolicies_ContextSpaceId",
                table: "GroupPolicies");

            migrationBuilder.DropColumn(
                name: "RecordVersion",
                table: "Revocations");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "Revocations");

            migrationBuilder.DropColumn(
                name: "ScopeOwnerId",
                table: "Revocations");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Relationships");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Reflections");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Observations");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "MemoryCandidates");

            migrationBuilder.DropColumn(
                name: "ScopeOwnerId",
                table: "MemoryCandidates");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "ScopeOwnerId",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "SharedFromMemoryId",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "AllowCrossGroupMemory",
                table: "LogicalUsers");

            migrationBuilder.DropColumn(
                name: "AllowReflection",
                table: "GroupPolicies");

            migrationBuilder.DropColumn(
                name: "ContextSpaceId",
                table: "GroupPolicies");

            migrationBuilder.DropColumn(
                name: "ProactiveEnabled",
                table: "GroupPolicies");

            migrationBuilder.CreateIndex(
                name: "IX_Relationships_BotId_UserId",
                table: "Relationships",
                columns: new[] { "BotId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memories_BotId_SubjectId_Status",
                table: "Memories",
                columns: new[] { "BotId", "SubjectId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Relationships_BotId_UserId",
                table: "Relationships");

            migrationBuilder.DropIndex(
                name: "IX_Memories_BotId_SubjectId_Status",
                table: "Memories");

            migrationBuilder.AddColumn<int>(
                name: "RecordVersion",
                table: "Revocations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "Revocations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScopeOwnerId",
                table: "Revocations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "GroupId",
                table: "Relationships",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupId",
                table: "Reflections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupId",
                table: "Observations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "MemoryCandidates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScopeOwnerId",
                table: "MemoryCandidates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "Memories",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScopeOwnerId",
                table: "Memories",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SharedFromMemoryId",
                table: "Memories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowCrossGroupMemory",
                table: "LogicalUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowReflection",
                table: "GroupPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ContextSpaceId",
                table: "GroupPolicies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ProactiveEnabled",
                table: "GroupPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ContextSpaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContextSpaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContextSpaces_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Revocations_Scope",
                table: "Revocations",
                sql: "\"Scope\" IN (0,1,2,3,4,5)");

            migrationBuilder.CreateIndex(
                name: "IX_Relationships_BotId_UserId_GroupId",
                table: "Relationships",
                columns: new[] { "BotId", "UserId", "GroupId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MemoryCandidates_Scope",
                table: "MemoryCandidates",
                sql: "\"Scope\" IN (0,1,2,3,4,5)");

            migrationBuilder.CreateIndex(
                name: "IX_Memories_BotId_Scope_ScopeOwnerId_Status",
                table: "Memories",
                columns: new[] { "BotId", "Scope", "ScopeOwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_SharedFromMemoryId_Scope_ScopeOwnerId",
                table: "Memories",
                columns: new[] { "SharedFromMemoryId", "Scope", "ScopeOwnerId" },
                unique: true,
                filter: "\"SharedFromMemoryId\" IS NOT NULL AND \"Status\" <> 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Memories_Scope",
                table: "Memories",
                sql: "\"Scope\" IN (0,1,2,3,4,5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Memory_Enums",
                table: "Memories",
                sql: "\"Scope\" BETWEEN 0 AND 5 AND \"Status\" BETWEEN 0 AND 2");

            migrationBuilder.CreateIndex(
                name: "IX_GroupPolicies_ContextSpaceId",
                table: "GroupPolicies",
                column: "ContextSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ContextSpaces_BotId",
                table: "ContextSpaces",
                column: "BotId");

            migrationBuilder.AddForeignKey(
                name: "FK_GroupPolicies_ContextSpaces_ContextSpaceId",
                table: "GroupPolicies",
                column: "ContextSpaceId",
                principalTable: "ContextSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Memories_Memories_SharedFromMemoryId",
                table: "Memories",
                column: "SharedFromMemoryId",
                principalTable: "Memories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
