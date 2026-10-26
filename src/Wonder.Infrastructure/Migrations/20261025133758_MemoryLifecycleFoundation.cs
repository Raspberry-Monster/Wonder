using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MemoryLifecycleFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BaseSummaryId",
                table: "Summaries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BaseSummaryRevision",
                table: "Summaries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProjectionBaseSummaryRevision",
                table: "Summaries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProjectionExpiresAt",
                table: "Summaries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProjectionRevision",
                table: "Summaries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ProjectionState",
                table: "Summaries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SafeProjectionJson",
                table: "Summaries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcesJson",
                table: "Summaries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOutdated",
                table: "Memories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "SourceRevision",
                table: "Embeddings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "MemoryOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ArgumentsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetMemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedRevision = table.Column<long>(type: "bigint", nullable: false),
                    ResultMemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryOperations_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MemoryOperations_LogicalUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "LogicalUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // A retained replacement link proves supersession; independent Archive does not.
            migrationBuilder.Sql("""
                UPDATE "Memories" old SET "IsOutdated" = TRUE
                WHERE old."ArchivedAt" IS NOT NULL AND old."Status" <> 2
                  AND (SELECT COUNT(*) FROM "Memories" n WHERE n."SupersedesMemoryId" = old."Id") = 1
                  AND EXISTS (SELECT 1 FROM "Memories" n WHERE n."SupersedesMemoryId" = old."Id"
                    AND n."BotId" = old."BotId" AND n."SubjectId" = old."SubjectId" AND n."Status" <> 2);
                UPDATE "Summaries" SET "ProjectionState" = 'Missing';
                -- Legacy vectors have no proven source version. Rebuild via the existing Outbox.
                INSERT INTO "Outbox" ("Id", "BotId", "CorrelationId", "SchemaVersion", "Type", "Payload",
                  "Status", "Attempts", "CreatedAt", "NextAttemptAt")
                SELECT gen_random_uuid(), m."BotId", gen_random_uuid(), 2, 'embedding',
                  '{"id":"' || m."Id"::text || '"}', 0, 0, now(), now()
                FROM "Memories" m JOIN "LogicalUsers" u ON u."Id" = m."SubjectId"
                WHERE m."Status" <> 2 AND m."ArchivedAt" IS NULL AND NOT m."IsOutdated" AND m."Text" <> '' AND u."AllowMemory"
                  AND NOT EXISTS (SELECT 1 FROM "Outbox" o WHERE o."BotId" = m."BotId" AND o."Type" = 'embedding'
                    AND o."Status" IN (0,1) AND (o."Payload"::jsonb->>'id') = m."Id"::text);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Summaries_ProjectionExpiresAt",
                table: "Summaries",
                column: "ProjectionExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_ActorId_IdempotencyKey",
                table: "MemoryOperations",
                columns: new[] { "ActorId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_BotId",
                table: "MemoryOperations",
                column: "BotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Memory lifecycle migrations are forward-only; retain operation receipts and fences.");
    }
}
