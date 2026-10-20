using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SharedContextAndUserNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Memory_Enums",
                table: "Memories");

            migrationBuilder.AddColumn<bool>(
                name: "ContextInvalidated",
                table: "Summaries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ContextInvalidated",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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

            migrationBuilder.AddColumn<string>(
                name: "PreferredName",
                table: "LogicalUsers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ContextSpaceId",
                table: "GroupPolicies",
                type: "uuid",
                nullable: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_Memories_SharedFromMemoryId_Scope_ScopeOwnerId",
                table: "Memories",
                columns: new[] { "SharedFromMemoryId", "Scope", "ScopeOwnerId" },
                unique: true,
                filter: "\"SharedFromMemoryId\" IS NOT NULL AND \"Status\" <> 2");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GroupPolicies_ContextSpaces_ContextSpaceId",
                table: "GroupPolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_Memories_Memories_SharedFromMemoryId",
                table: "Memories");

            migrationBuilder.DropTable(
                name: "ContextSpaces");

            migrationBuilder.DropIndex(
                name: "IX_Memories_SharedFromMemoryId_Scope_ScopeOwnerId",
                table: "Memories");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Memory_Enums",
                table: "Memories");

            migrationBuilder.DropIndex(
                name: "IX_GroupPolicies_ContextSpaceId",
                table: "GroupPolicies");

            migrationBuilder.DropColumn(
                name: "ContextInvalidated",
                table: "Summaries");

            migrationBuilder.DropColumn(
                name: "ContextInvalidated",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SharedFromMemoryId",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "AllowCrossGroupMemory",
                table: "LogicalUsers");

            migrationBuilder.DropColumn(
                name: "PreferredName",
                table: "LogicalUsers");

            migrationBuilder.DropColumn(
                name: "ContextSpaceId",
                table: "GroupPolicies");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Memory_Enums",
                table: "Memories",
                sql: "\"Scope\" BETWEEN 0 AND 3 AND \"Status\" BETWEEN 0 AND 2");
        }
    }
}
