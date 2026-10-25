namespace Wonder.Infrastructure.Migrations;

// A one-time, transactionally archived cutover. This schema has no application entity or API.
internal static class RetiredContextArchive
{
    internal const string Prepare = """
        CREATE SCHEMA retired_context;
        CREATE TABLE retired_context.entries (
          source_table text NOT NULL, source_id text NOT NULL, row_json jsonb NOT NULL,
          archived_at timestamptz NOT NULL DEFAULT clock_timestamp(), PRIMARY KEY (source_table, source_id));
        CREATE TEMP TABLE retired_sources (id uuid PRIMARY KEY) ON COMMIT DROP;
        INSERT INTO retired_sources
          SELECT "Id" FROM "Memories" WHERE "Scope" <> 0 OR "SharedFromMemoryId" IS NOT NULL
          UNION SELECT m."Id" FROM "Messages" m JOIN "Conversations" c ON c."Id" = m."ConversationId" WHERE c."Kind" = 1
          UNION SELECT "Id" FROM "Reflections" WHERE "GroupId" IS NOT NULL
          UNION SELECT "Id" FROM "MemoryCandidates" WHERE "Scope" <> 0;
        -- Missing/corrupt provenance cannot establish that an old cached answer is unrelated.
        -- Keep proven unrelated private caches; archive and isolate only unattributed caches in affected bots.
        INSERT INTO retired_sources SELECT m."Id" FROM "Messages" m WHERE m."Role" = 'assistant' AND m."BotId" IN
          (SELECT "BotId" FROM "Memories" WHERE "Scope" <> 0 OR "SharedFromMemoryId" IS NOT NULL
           UNION SELECT "BotId" FROM "Conversations" WHERE "Kind" = 1)
          AND CASE WHEN m."ContextSnapshotJson" IS JSON OBJECT
            THEN jsonb_typeof(m."ContextSnapshotJson"::jsonb->'retrievedMemoryIds') IS DISTINCT FROM 'array'
            ELSE true END
          ON CONFLICT DO NOTHING;
        INSERT INTO retired_sources SELECT s."Id" FROM "Summaries" s WHERE s."BotId" IN
          (SELECT "BotId" FROM "Memories" WHERE "Scope" <> 0 OR "SharedFromMemoryId" IS NOT NULL
           UNION SELECT "BotId" FROM "Conversations" WHERE "Kind" = 1)
          AND CASE WHEN s."MessageIdsJson" IS JSON ARRAY THEN jsonb_array_length(s."MessageIdsJson"::jsonb) = 0 ELSE true END
          ON CONFLICT DO NOTHING;
        DO $lineage$
        DECLARE changed integer; added integer;
        BEGIN
          LOOP
            changed := 0;
            INSERT INTO retired_sources SELECT m."Id" FROM "Messages" m WHERE m."ContextSnapshotJson" IS NOT NULL
              AND EXISTS (SELECT 1 FROM retired_sources r WHERE lower(m."ContextSnapshotJson") LIKE '%' || r.id::text || '%')
              ON CONFLICT DO NOTHING;
            GET DIAGNOSTICS added = ROW_COUNT; changed := changed + added;
            INSERT INTO retired_sources SELECT s."Id" FROM "Summaries" s WHERE
              EXISTS (SELECT 1 FROM retired_sources r WHERE lower(s."MemoryIdsJson" || s."MessageIdsJson") LIKE '%' || r.id::text || '%')
              OR EXISTS (SELECT 1 FROM "Sessions" se JOIN "Conversations" c ON se."ConversationId" = c."Id" WHERE se."Id" = s."SessionId" AND c."Kind" = 1)
              ON CONFLICT DO NOTHING;
            GET DIAGNOSTICS added = ROW_COUNT; changed := changed + added;
            INSERT INTO retired_sources SELECT n."Id" FROM "Notes" n WHERE EXISTS
              (SELECT 1 FROM retired_sources r WHERE lower(n."SourceMemoryIdsJson" || n."SourceMessageIdsJson") LIKE '%' || r.id::text || '%')
              ON CONFLICT DO NOTHING;
            GET DIAGNOSTICS added = ROW_COUNT; changed := changed + added;
            INSERT INTO retired_sources SELECT d."TargetId" FROM "NoteDependencies" d JOIN retired_sources r ON d."NoteId" = r.id
              ON CONFLICT DO NOTHING;
            GET DIAGNOSTICS added = ROW_COUNT; changed := changed + added;
            INSERT INTO retired_sources SELECT j."Id" FROM "Outbox" j JOIN "Messages" m ON m."ExternalMessageId" = 'generated:' || j."Id"::text
              JOIN retired_sources r ON m."Id" = r.id WHERE j."Type" = 'chat'
              ON CONFLICT DO NOTHING;
            GET DIAGNOSTICS added = ROW_COUNT; changed := changed + added;
            EXIT WHEN changed = 0;
          END LOOP;
        END $lineage$;
        CREATE TEMP TABLE retired_jobs (id uuid PRIMARY KEY) ON COMMIT DROP;
        INSERT INTO retired_jobs SELECT j."Id" FROM "Outbox" j WHERE j."Id" IN (SELECT id FROM retired_sources)
          OR j."ConversationId" IN (SELECT "Id" FROM "Conversations" WHERE "Kind" = 1)
          OR (j."Type" IN ('embedding','emotion','relationship','memory','memory-maintenance') AND EXISTS
            (SELECT 1 FROM retired_sources r WHERE lower(j."Payload") LIKE '%' || r.id::text || '%'));

        INSERT INTO retired_context.entries SELECT 'Memories', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Memories" t WHERE "Scope" <> 0 OR "SharedFromMemoryId" IS NOT NULL;
        INSERT INTO retired_context.entries SELECT 'Embeddings', "MemoryId"::text || ':' || "ProfileId"::text, to_jsonb(t), clock_timestamp() FROM "Embeddings" t WHERE "MemoryId" IN (SELECT id FROM retired_sources);
        INSERT INTO retired_context.entries SELECT 'Reflections', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Reflections" t WHERE "GroupId" IS NOT NULL;
        INSERT INTO retired_context.entries SELECT 'MemoryCandidates', "Id"::text, to_jsonb(t), clock_timestamp() FROM "MemoryCandidates" t WHERE "Scope" <> 0;
        INSERT INTO retired_context.entries SELECT 'Relationships', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Relationships" t WHERE "GroupId" IS NOT NULL;
        INSERT INTO retired_context.entries SELECT 'Observations', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Observations" t WHERE "GroupId" IS NOT NULL;
        INSERT INTO retired_context.entries SELECT 'EmotionEvents', "Id"::text, to_jsonb(t), clock_timestamp() FROM "EmotionEvents" t WHERE "SourceReflectionId" IN (SELECT id FROM retired_sources);
        INSERT INTO retired_context.entries SELECT 'Revocations', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Revocations" t;
        INSERT INTO retired_context.entries SELECT 'ContextSpaces', "Id"::text, to_jsonb(t), clock_timestamp() FROM "ContextSpaces" t;
        INSERT INTO retired_context.entries SELECT 'GroupPolicies', "Id"::text, to_jsonb(t), clock_timestamp() FROM "GroupPolicies" t;
        INSERT INTO retired_context.entries SELECT 'Conversations', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Conversations" t WHERE "Kind" = 1;
        INSERT INTO retired_context.entries SELECT 'Sessions', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Sessions" t WHERE "ConversationId" IN (SELECT "Id" FROM "Conversations" WHERE "Kind" = 1)
          OR "Id" IN (SELECT "SessionId" FROM "Messages" WHERE "Id" IN (SELECT id FROM retired_sources));
        INSERT INTO retired_context.entries SELECT 'Media', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Media" t WHERE "MessageId" IN (SELECT id FROM retired_sources);
        INSERT INTO retired_context.entries SELECT 'EmbeddingProfiles', "Id"::text, to_jsonb(t), clock_timestamp() FROM "EmbeddingProfiles" t WHERE "Id" IN
          (SELECT "ProfileId" FROM "Embeddings" WHERE "MemoryId" IN (SELECT id FROM retired_sources));
        INSERT INTO retired_context.entries SELECT 'Messages', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Messages" t WHERE "Id" IN (SELECT id FROM retired_sources);
        INSERT INTO retired_context.entries SELECT 'Summaries', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Summaries" t WHERE "Id" IN (SELECT id FROM retired_sources);
        INSERT INTO retired_context.entries SELECT 'Notes', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Notes" t WHERE "Id" IN (SELECT id FROM retired_sources);
        INSERT INTO retired_context.entries SELECT 'NoteRevisions', "NoteId"::text || ':' || "Version"::text, to_jsonb(t), clock_timestamp() FROM "NoteRevisions" t WHERE "NoteId" IN (SELECT id FROM retired_sources);
        INSERT INTO retired_context.entries SELECT 'NoteDependencies', "NoteId"::text || ':' || "Version"::text || ':' || "TargetKind" || ':' || "TargetId"::text, to_jsonb(t), clock_timestamp() FROM "NoteDependencies" t WHERE "NoteId" IN (SELECT id FROM retired_sources) OR "TargetId" IN (SELECT id FROM retired_sources);
        INSERT INTO retired_context.entries SELECT 'Outbox', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Outbox" t WHERE "Id" IN (SELECT id FROM retired_jobs);
        INSERT INTO retired_context.entries SELECT 'Inbox', "EventId"::text || ':' || "HandlerName", to_jsonb(t), clock_timestamp() FROM "Inbox" t WHERE "EventId" IN (SELECT id FROM retired_jobs);
        INSERT INTO retired_context.entries SELECT 'ReflectionGenerations', "Id"::text, to_jsonb(t), clock_timestamp() FROM "ReflectionGenerations" t WHERE "JobId" IN (SELECT id FROM retired_jobs)
          ON CONFLICT (source_table, source_id) DO NOTHING;
        INSERT INTO retired_context.entries SELECT 'AgentReflectionRequests', "Id"::text, to_jsonb(t), clock_timestamp() FROM "AgentReflectionRequests" t WHERE "GenerationId" IN
          (SELECT "Id" FROM "ReflectionGenerations" WHERE "JobId" IN (SELECT id FROM retired_jobs)) ON CONFLICT (source_table, source_id) DO NOTHING;
        INSERT INTO retired_context.entries SELECT 'Batches', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Batches" t WHERE "SourceJobId" IN (SELECT id FROM retired_jobs);
        INSERT INTO retired_context.entries SELECT 'Parts', "Id"::text, to_jsonb(t), clock_timestamp() FROM "Parts" t WHERE "BatchId" IN (SELECT "Id" FROM "Batches" WHERE "SourceJobId" IN (SELECT id FROM retired_jobs));

        UPDATE "Messages" SET "ContextInvalidated" = true, "AllowReflection" = false WHERE "Id" IN (SELECT id FROM retired_sources);
        UPDATE "Summaries" SET "ContextInvalidated" = true WHERE "Id" IN (SELECT id FROM retired_sources);
        UPDATE "Notes" SET "SourceInvalidated" = true WHERE "Id" IN (SELECT id FROM retired_sources);
        UPDATE "ReflectionGenerations" SET "Status" = 'Cancelled', "CancellationReason" = 'SourceChanged' WHERE "JobId" IN (SELECT id FROM retired_jobs) AND "Status" <> 'Completed';
        UPDATE "AgentReflectionRequests" SET "Reason" = '[Source retired]' WHERE "GenerationId" IN (SELECT "Id" FROM "ReflectionGenerations" WHERE "JobId" IN (SELECT id FROM retired_jobs));
        UPDATE "Parts" SET "Status" = CASE WHEN "Status" = 0 THEN 4 WHEN "Status" = 1 THEN 3 ELSE "Status" END
          WHERE "BatchId" IN (SELECT "Id" FROM "Batches" WHERE "SourceJobId" IN (SELECT id FROM retired_jobs));
        UPDATE "Outbox" SET "Status" = 4, "ErrorCode" = 'RetiredContext', "LeaseId" = NULL, "LeaseUntil" = NULL, "ProgressJson" = NULL
          WHERE "Id" IN (SELECT id FROM retired_jobs) AND "Status" IN (0,1,3);
        UPDATE "Revocations" SET "ResourceKind" = 'MemoryContext', "FactKeyHash" = '', "ResourceId" = NULL
          WHERE "Scope" <> 0 AND "ResourceKind" = 'Memory';
        UPDATE "Memories" SET "SharedFromMemoryId" = NULL WHERE "Scope" <> 0 OR "SharedFromMemoryId" IS NOT NULL;
        DELETE FROM "Embeddings" WHERE "MemoryId" IN (SELECT id FROM retired_sources);
        DELETE FROM "Memories" WHERE "Id"::text IN (SELECT source_id FROM retired_context.entries WHERE source_table = 'Memories');
        DELETE FROM "MemoryCandidates" WHERE "Scope" <> 0;
        DELETE FROM "Reflections" WHERE "GroupId" IS NOT NULL;
        DELETE FROM "EmotionEvents" WHERE "SourceReflectionId" IN (SELECT id FROM retired_sources);
        DELETE FROM "Relationships" WHERE "GroupId" IS NOT NULL;
        DELETE FROM "Observations" WHERE "GroupId" IS NOT NULL;
        CREATE FUNCTION retired_context.reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $guard$
          BEGIN RAISE EXCEPTION 'Retired context archive is read-only'; END $guard$;
        CREATE TRIGGER archive_read_only BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON retired_context.entries
          FOR EACH STATEMENT EXECUTE FUNCTION retired_context.reject_mutation();
        REVOKE ALL ON SCHEMA retired_context FROM PUBLIC;
        REVOKE ALL ON ALL TABLES IN SCHEMA retired_context FROM PUBLIC;
        """;
}
