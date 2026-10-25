using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TypedBusinessTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $validate$ BEGIN
                  IF EXISTS (SELECT 1 FROM "ChatDispositions" WHERE "Outcome" <> 'NoReply') THEN
                    RAISE EXCEPTION 'Unknown ChatDisposition outcome; resolve it before migrating';
                  END IF;
                END $validate$;
                """);
            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "ChatDispositions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Usage_TaskKind",
                table: "Usage",
                sql: "\"TaskKind\" IN (0,1,2,3,4,5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sessions_Status",
                table: "Sessions",
                sql: "\"Status\" IN (0,1,2,3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Revocations_ResourceKind",
                table: "Revocations",
                sql: "\"ResourceKind\" IN ('Memory','Note','MemoryContext')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Revocations_Scope",
                table: "Revocations",
                sql: "\"Scope\" IN (0,1,2,3,4,5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reflections_Kind",
                table: "Reflections",
                sql: "\"Kind\" IN ('light','full')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReflectionGenerations_CancellationReason",
                table: "ReflectionGenerations",
                sql: "\"CancellationReason\" IN ('AuthorizationChanged','SourceChanged','NoEligibleSources','PrivacyRevocation')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReflectionGenerations_Status",
                table: "ReflectionGenerations",
                sql: "\"Status\" IN ('Queued','Running','Completed','Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parts_Status",
                table: "Parts",
                sql: "\"Status\" IN (0,1,2,3,4)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Outbox_Status",
                table: "Outbox",
                sql: "\"Status\" IN (0,1,2,3,4)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Outbox_Type",
                table: "Outbox",
                sql: "\"Type\" IN ('chat','outgoing','reflection','emotion','relationship','memory','memory-maintenance','embedding','maintenance','narrative','summary','deletion-ledger')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notes_AuthorKind",
                table: "Notes",
                sql: "\"AuthorKind\" IN ('Agent','User')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notes_ClaimKind",
                table: "Notes",
                sql: "\"ClaimKind\" IN ('observation','guess','uncertain')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NoteDependencies_TargetKind",
                table: "NoteDependencies",
                sql: "\"TargetKind\" IN ('Chat','Message','Summary','Note')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Models_TaskKind",
                table: "Models",
                sql: "\"TaskKind\" IN (0,1,2,3,4,5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Role",
                table: "Messages",
                sql: "\"Role\" IN ('user','assistant')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MemoryCandidates_Action",
                table: "MemoryCandidates",
                sql: "\"Action\" IN (0,1,2,3,4,5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MemoryCandidates_Scope",
                table: "MemoryCandidates",
                sql: "\"Scope\" IN (0,1,2,3,4,5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MemoryCandidates_Status",
                table: "MemoryCandidates",
                sql: "\"Status\" IN (0,1,2,3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Memories_Scope",
                table: "Memories",
                sql: "\"Scope\" IN (0,1,2,3,4,5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Memories_Status",
                table: "Memories",
                sql: "\"Status\" IN (0,1,2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Inbox_HandlerName",
                table: "Inbox",
                sql: "\"HandlerName\" IN ('chat','outgoing','reflection','emotion','relationship','memory','memory-maintenance','embedding','maintenance','narrative','summary','deletion-ledger')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ForceReceipts_CompensationStatus",
                table: "ForceReceipts",
                sql: "\"CompensationStatus\" IN ('Refunded','AlreadyProcessed','Expired','SupersededByReset')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_Kind",
                table: "Conversations",
                sql: "\"Kind\" IN (0,1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AgentReflectionRequests_Priority",
                table: "AgentReflectionRequests",
                sql: "\"Priority\" IN ('Normal','Important')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AgentOperations_Action",
                table: "AgentOperations",
                sql: "\"Action\" IN ('interaction_mute','interaction_reset_force','interaction_unmute','interaction_no_reply','notes_create','notes_search','notes_read','notes_update','notes_delete','reflection_request_review','reflection_list_runs','reflection_get_run')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Usage_TaskKind",
                table: "Usage");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sessions_Status",
                table: "Sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Revocations_ResourceKind",
                table: "Revocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Revocations_Scope",
                table: "Revocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reflections_Kind",
                table: "Reflections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReflectionGenerations_CancellationReason",
                table: "ReflectionGenerations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReflectionGenerations_Status",
                table: "ReflectionGenerations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parts_Status",
                table: "Parts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Outbox_Status",
                table: "Outbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Outbox_Type",
                table: "Outbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Notes_AuthorKind",
                table: "Notes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Notes_ClaimKind",
                table: "Notes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NoteDependencies_TargetKind",
                table: "NoteDependencies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Models_TaskKind",
                table: "Models");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Role",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MemoryCandidates_Action",
                table: "MemoryCandidates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MemoryCandidates_Scope",
                table: "MemoryCandidates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MemoryCandidates_Status",
                table: "MemoryCandidates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Memories_Scope",
                table: "Memories");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Memories_Status",
                table: "Memories");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Inbox_HandlerName",
                table: "Inbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ForceReceipts_CompensationStatus",
                table: "ForceReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_Kind",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AgentReflectionRequests_Priority",
                table: "AgentReflectionRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AgentOperations_Action",
                table: "AgentOperations");

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "ChatDispositions",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
