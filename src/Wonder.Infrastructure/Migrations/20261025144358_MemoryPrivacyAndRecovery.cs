using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MemoryPrivacyAndRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Outbox_Type",
                table: "Outbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Inbox_HandlerName",
                table: "Inbox");

            migrationBuilder.AddColumn<bool>(
                name: "MemorySourcesBlocked",
                table: "Reflections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "Parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssertionHashesJson",
                table: "MemoryOperations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "CleanupVerified",
                table: "MemoryOperations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                table: "MemoryOperations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Fenced",
                table: "MemoryOperations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreviewExpiresAt",
                table: "MemoryOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectionReceiptJson",
                table: "MemoryOperations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecoveryVerified",
                table: "MemoryOperations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "MemoryOperations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ScopeHash",
                table: "MemoryOperations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "MemoryOperations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TargetsJson",
                table: "MemoryOperations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AssertionsJson",
                table: "MemoryCandidates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemNotice",
                table: "Batches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SourceSnapshotJson",
                table: "Batches",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EvidenceSuppressions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageVersion = table.Column<string>(type: "text", nullable: false),
                    FactKeyHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceSuppressions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvidenceSuppressions_MemoryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "MemoryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemoryOperationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVersion = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryOperationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryOperationItems_MemoryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "MemoryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RelationshipClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Origin = table.Column<string>(type: "text", nullable: false),
                    Qualification = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    MemorySourcesJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelationshipClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RelationshipClaims_Relationships_RelationshipId",
                        column: x => x.RelationshipId,
                        principalTable: "Relationships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Outbox_Type",
                table: "Outbox",
                sql: "\"Type\" IN ('chat','outgoing','reflection','emotion','relationship','memory','memory-maintenance','embedding','maintenance','narrative','summary','deletion-ledger','memory-privacy')");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_Stage_CreatedAt",
                table: "MemoryOperations",
                columns: new[] { "Stage", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Inbox_HandlerName",
                table: "Inbox",
                sql: "\"HandlerName\" IN ('chat','outgoing','reflection','emotion','relationship','memory','memory-maintenance','embedding','maintenance','narrative','summary','deletion-ledger','memory-privacy')");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceSuppressions_BotId_SubjectId_MessageId",
                table: "EvidenceSuppressions",
                columns: new[] { "BotId", "SubjectId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceSuppressions_OperationId_MemoryId_MessageId",
                table: "EvidenceSuppressions",
                columns: new[] { "OperationId", "MemoryId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperationItems_OperationId_Kind_ResourceId",
                table: "MemoryOperationItems",
                columns: new[] { "OperationId", "Kind", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipClaims_BotId_SubjectId",
                table: "RelationshipClaims",
                columns: new[] { "BotId", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipClaims_RelationshipId",
                table: "RelationshipClaims",
                column: "RelationshipId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Privacy migrations are forward-only.");
    }
}
