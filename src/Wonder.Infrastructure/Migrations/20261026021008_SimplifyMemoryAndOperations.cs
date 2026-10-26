using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyMemoryAndOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Retired jobs remain readable for diagnostics but can never run or retain drafts.
            migrationBuilder.Sql("""
                DELETE FROM "Models" WHERE "TaskKind" = 3;
                UPDATE "Outbox" SET "Status" = 4, "ProgressJson" = NULL,
                    "LeaseId" = NULL, "LeaseUntil" = NULL, "ErrorCode" = 'FeatureRetired'
                    WHERE "Type" IN ('narrative', 'deletion-ledger');

                -- Recheck online results, including old completed operations. Never invent recovery receipts.
                UPDATE "MemoryOperationItems" i SET "State" = 'Pending', "ErrorCode" = NULL,
                    "VerifiedAt" = NULL, "Revision" = i."Revision" + 1
                    FROM "MemoryOperations" o WHERE i."OperationId" = o."Id" AND o."Fenced"
                    AND o."Kind" IN ('Forget', 'Erase');
                UPDATE "MemoryOperations" SET "Stage" = 'PendingCleanup', "CleanupVerified" = false,
                    "ErrorCode" = NULL, "Revision" = "Revision" + 1
                    WHERE "Fenced" AND "Kind" IN ('Forget', 'Erase');
                UPDATE "Outbox" j SET "Status" = 0, "Attempts" = 0, "ProgressJson" = NULL,
                    "LeaseId" = NULL, "LeaseUntil" = NULL, "ErrorCode" = NULL, "NextAttemptAt" = now()
                    FROM "MemoryOperations" o WHERE j."Type" = 'memory-privacy'
                    AND j."Payload"::jsonb->>'id' = o."Id"::text AND o."Fenced" AND o."Kind" IN ('Forget', 'Erase');
                DELETE FROM "Inbox" i USING "Outbox" j WHERE i."EventId" = j."Id"
                    AND j."Type" = 'memory-privacy' AND j."Status" = 0;
                INSERT INTO "Outbox" ("Id", "BotId", "CorrelationId", "SchemaVersion", "Type", "Payload",
                    "Status", "Attempts", "CreatedAt", "NextAttemptAt")
                    SELECT gen_random_uuid(), o."BotId", o."Id", 2, 'memory-privacy',
                        json_build_object('id', o."Id")::text, 0, 0, now(), now()
                    FROM "MemoryOperations" o WHERE o."Fenced" AND o."Kind" IN ('Forget', 'Erase')
                    AND NOT EXISTS (SELECT 1 FROM "Outbox" j WHERE j."Type" = 'memory-privacy'
                        AND j."Payload"::jsonb->>'id' = o."Id"::text AND j."Status" IN (0, 1));
                """);

            migrationBuilder.DropTable(
                name: "Narratives");

            migrationBuilder.DropColumn(
                name: "ProtectionReceiptJson",
                table: "MemoryOperations");

            migrationBuilder.DropColumn(
                name: "RecoveryVerified",
                table: "MemoryOperations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Feature removal and privacy migrations are forward-only.");
    }
}
