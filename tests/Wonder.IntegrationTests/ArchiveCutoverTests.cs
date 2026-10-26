using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;
using static Wonder.IntegrationTests.InteractionTests;

namespace Wonder.IntegrationTests;

public sealed class ArchiveCutoverTests
{
    [Test]
    public async Task PostgreSqlUnknownLegacyOutcomeAbortsBeforeDroppingItsColumn()
    {
        await using var pg = await TestPostgres.CreateAsync("20261024144500_VersionedNotesAndPrivacyLedger");
        using var app = pg.CreateApp();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"ChatDispositions\" (\"ChatJobId\",\"ForceReceiptId\",\"OperationId\",\"Outcome\",\"CommittedAt\") VALUES ({Guid.NewGuid()},{Guid.NewGuid()},{Guid.NewGuid()},'Unknown',{DateTimeOffset.UtcNow})");
        await Assert.That(async () => { await db.Database.MigrateAsync(); }).ThrowsExactly<PostgresException>();
        var outcome = await db.Database.SqlQueryRaw<string>("SELECT \"Outcome\" AS \"Value\" FROM \"ChatDispositions\"")
            .SingleAsync();
        await Assert.That(outcome).IsEqualTo("Unknown");
    }

    [Test]
    public async Task PostgreSqlCutoverArchivesSourcesBeforeRetiringTheirOnlineLineage()
    {
        await using var pg = await TestPostgres.CreateAsync("20261024184515_TypedBusinessTags");
        using var app = pg.CreateApp();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        // These defaults let the current private-only model seed the old schema during upgrade tests.
        await db.Database.ExecuteSqlRawAsync("""
                                             ALTER TABLE "LogicalUsers" ALTER COLUMN "AllowCrossGroupMemory" SET DEFAULT false;
                                             ALTER TABLE "Memories" ALTER COLUMN "Scope" SET DEFAULT 0;
                                             ALTER TABLE "Memories" ALTER COLUMN "ScopeOwnerId" SET DEFAULT '';
                                             """);
        var f = await SeedAsync(app);
        var userId = await db.LogicalUsers.Select(x => x.Id).SingleAsync();
        var original = new Memory
        {
            BotId = f.Bot, SubjectId = userId, FactKey = "private", Text = "private fact", Status = MemoryStatus.Active
        };
        // Seed the actual historical schema; the current EF model includes Gate 1 columns.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
                                                       INSERT INTO "Memories" ("Id","BotId","SubjectId","FactKey","Text","Status","SourceReflectionId","EvidenceJson","CreatedAt","FactKeyHash","Revision")
                                                       VALUES ({original.Id},{f.Bot},{userId},'private','private fact',1,{Guid.Empty},'[]',{DateTimeOffset.UtcNow},'',1)
                                                       """);
        var sharedId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
                                                       INSERT INTO "Memories" ("Id","BotId","SubjectId","Scope","ScopeOwnerId","SharedFromMemoryId","FactKey","Text","Status","SourceReflectionId","EvidenceJson","CreatedAt","FactKeyHash","Revision")
                                                       VALUES ({sharedId},{f.Bot},{userId},4,{userId.ToString()},{original.Id},'private','archived shared fact',1,{Guid.Empty},'[]',{DateTimeOffset.UtcNow},'',1)
                                                       """);
        var conversation = new Conversation { BotId = f.Bot, ExternalId = "private-cache" };
        db.Conversations.Add(conversation);
        var session = new ConversationSession { BotId = f.Bot, ConversationId = conversation.Id };
        db.Sessions.Add(session);
        var job = Jobs.Add(db, f.Bot, JobKind.Chat, new IdPayload(Guid.NewGuid()), conversation.Id);
        var reply = new ChatMessage
        {
            BotId = f.Bot, ConversationId = conversation.Id, SessionId = session.Id,
            Role = MessageRole.Assistant, ExternalMessageId = "generated:" + job.Id, Text = "shared-derived reply",
            ContextSnapshotJson = Json.Write(new ContextSnapshot(f.Bot, session.Id, 1, 1, 0, 0, [sharedId], "test"))
        };
        db.Messages.Add(reply);
        var unproven = new ChatMessage
        {
            BotId = f.Bot, ConversationId = conversation.Id, SessionId = session.Id,
            Role = MessageRole.Assistant, ExternalMessageId = "unattributed-old-answer",
            Text = "old answer without provenance"
        };
        var unrelated = new ChatMessage
        {
            BotId = f.Bot, ConversationId = conversation.Id, SessionId = session.Id,
            Role = MessageRole.Assistant, ExternalMessageId = "unrelated-answer", Text = "unrelated private answer",
            ContextSnapshotJson = Json.Write(new ContextSnapshot(f.Bot, session.Id, 1, 1, 0, 0, [], "test"))
        };
        db.Messages.AddRange(unproven, unrelated);
        var unknownSession = new ConversationSession
            { BotId = f.Bot, ConversationId = conversation.Id, Status = SessionStatus.Closed };
        var unrelatedSession = new ConversationSession
            { BotId = f.Bot, ConversationId = conversation.Id, Status = SessionStatus.Closed };
        db.Sessions.AddRange(unknownSession, unrelatedSession);
        var unknownSummary = new ConversationSummary
            { BotId = f.Bot, SessionId = unknownSession.Id, Text = "old unattributed summary" };
        var unrelatedSummary = new ConversationSummary
        {
            BotId = f.Bot, SessionId = unrelatedSession.Id, ThroughSequence = 1,
            Text = "proven unrelated summary", MessageIdsJson = Json.Write(new[] { unrelated.Id })
        };
        var note = new Note
        {
            BotId = f.Bot, UserId = userId, ConversationId = conversation.Id, Content = "derived observation",
            SourceMemoryIdsJson = Json.Write(new[] { sharedId })
        };
        var independent = new Note
            { BotId = f.Bot, UserId = userId, ConversationId = conversation.Id, Content = "independent note" };
        db.Notes.AddRange(note, independent);
        db.NoteRevisions.Add(new NoteRevision { NoteId = note.Id, Version = 1, Content = note.Content });
        var group = new Conversation { BotId = f.Bot, Kind = ConversationKind.Group, ExternalId = "old-group" };
        var groupSession = new ConversationSession { BotId = f.Bot, ConversationId = group.Id };
        db.Conversations.Add(group);
        db.Sessions.Add(groupSession);
        var groupJob = Jobs.Add(db, f.Bot, JobKind.Reflection,
            new ReflectionJob(groupSession.Id, userId, 0, 0, ReflectionKind.Full), group.Id);
        var groupRun = new ReflectionGeneration
        {
            BotId = f.Bot, SessionId = groupSession.Id, SubjectId = userId,
            SourceSignature = Json.Hash("old-group-source"), JobId = groupJob.Id
        };
        db.ReflectionGenerations.Add(groupRun);
        var request = new AgentReflectionRequest
        {
            BotId = f.Bot, UserId = userId, ConversationId = group.Id,
            SessionId = groupSession.Id, GenerationId = groupRun.Id, Reason = "old group reason"
        };
        db.AgentReflectionRequests.Add(request);
        await db.SaveChangesAsync();
        foreach (var summary in new[] { unknownSummary, unrelatedSummary })
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                                                           INSERT INTO "Summaries" ("Id","BotId","SessionId","ThroughSequence","SubjectIdsJson","MemoryIdsJson","MessageIdsJson","ContextInvalidated","Revision","Text")
                                                           VALUES ({summary.Id},{summary.BotId},{summary.SessionId},{summary.ThroughSequence},{summary.SubjectIdsJson},{summary.MemoryIdsJson},{summary.MessageIdsJson},false,0,{summary.Text})
                                                           """);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        await Assert.That(await db.Memories.AnyAsync(x => x.Id == sharedId)).IsFalse();
        await Assert.That((await db.Memories.SingleAsync(x => x.Id == original.Id)).Text).IsEqualTo("private fact");
        await Assert.That((await db.Messages.SingleAsync(x => x.Id == reply.Id)).ContextInvalidated).IsTrue();
        await Assert.That((await db.Messages.SingleAsync(x => x.Id == unproven.Id)).ContextInvalidated).IsTrue();
        await Assert.That((await db.Messages.SingleAsync(x => x.Id == unrelated.Id)).ContextInvalidated).IsFalse();
        await Assert.That((await db.Summaries.SingleAsync(x => x.Id == unknownSummary.Id)).ContextInvalidated).IsTrue();
        await Assert.That((await db.Summaries.SingleAsync(x => x.Id == unrelatedSummary.Id)).ContextInvalidated)
            .IsFalse();
        await Assert.That((await db.Notes.SingleAsync(x => x.Id == note.Id)).SourceInvalidated).IsTrue();
        await Assert.That((await db.Notes.SingleAsync(x => x.Id == independent.Id)).SourceInvalidated).IsFalse();
        await Assert.That((await db.Outbox.SingleAsync(x => x.Id == job.Id)).Status).IsEqualTo(JobStatus.Cancelled);
        var archived = await db.Database
            .SqlQuery<string>(
                $"SELECT row_json->>'Text' AS \"Value\" FROM retired_context.entries WHERE source_table = 'Memories' AND source_id = {sharedId.ToString()}")
            .SingleAsync();
        await Assert.That(archived).IsEqualTo("archived shared fact");
        await Assert.That((await db.ReflectionGenerations.SingleAsync(x => x.Id == groupRun.Id)).Status)
            .IsEqualTo(ReflectionGenerationStatus.Cancelled);
        var archivedReason = await db.Database
            .SqlQuery<string>(
                $"SELECT row_json->>'Reason' AS \"Value\" FROM retired_context.entries WHERE source_table = 'AgentReflectionRequests' AND source_id = {request.Id.ToString()}")
            .SingleAsync();
        await Assert.That(archivedReason).IsEqualTo("old group reason");
        await Assert.That(async () =>
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE retired_context.entries SET row_json = row_json");
            })
            .ThrowsExactly<PostgresException>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        await Assert.That(await queue.TryClaimAsync([JobKind.Chat], default)).IsNull();
        await scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(job.Id, default);
        db.ChangeTracker.Clear();
        await Assert.That((await db.Outbox.SingleAsync(x => x.Id == job.Id)).Status).IsEqualTo(JobStatus.Cancelled);
        await Assert.That(app.ModelRequests).IsEmpty();
    }


    [Test]
    public async Task PostgreSqlRejectsUndefinedBusinessTagsWithoutChangingStoredValues()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "typed-tags", "hello");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var note = new Note
        {
            BotId = f.Bot, UserId = e.UserId, ConversationId = e.ConversationId, Content = "observation",
            ClaimKind = NoteClaimKind.Guess
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        await Assert.That(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Notes\" SET \"AuthorKind\" = 'Unknown' WHERE \"Id\" = {note.Id}");
        }).ThrowsExactly<PostgresException>();
        await Assert.That(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Outbox\" SET \"Type\" = 'unknown-job' WHERE \"Id\" = {e.ChatJobId}");
        }).ThrowsExactly<PostgresException>();
        db.ChangeTracker.Clear();
        await Assert.That((await db.Notes.SingleAsync()).ClaimKind).IsEqualTo(NoteClaimKind.Guess);
        await Assert.That((await db.Outbox.SingleAsync(x => x.Id == e.ChatJobId)).Type).IsEqualTo(JobKind.Chat);
    }
}