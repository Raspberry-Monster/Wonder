using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class MemoryLifecycleTests
{
    [Test]
    public async Task CorrectionPreservesIndependentBodiesAndEligibility()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { UserName = "gate1" };
        db.Users.Add(owner);
        var bot = new BotInstance { OwnerId = owner.Id };
        var otherBot = new BotInstance { OwnerId = owner.Id };
        var user = new LogicalUser { AllowMemory = true, RetainMemoryHistory = true };
        var otherUser = new LogicalUser { AllowMemory = true };
        db.Bots.AddRange(bot, otherBot);
        db.LogicalUsers.AddRange(user, otherUser);
        var conversation = new Conversation { BotId = bot.Id };
        db.Conversations.Add(conversation);
        var sessions = Enumerable.Range(0, 2).Select(_ => new ConversationSession
            { BotId = bot.Id, ConversationId = conversation.Id, Status = SessionStatus.Closed }).ToArray();
        db.Sessions.AddRange(sessions);
        var memory = new Memory { BotId = bot.Id, SubjectId = user.Id, FactKey = "color", Text = "blue" };
        var independent = new Memory { BotId = bot.Id, SubjectId = user.Id, FactKey = "food", Text = "rice" };
        var otherAuthor = new Memory { BotId = bot.Id, SubjectId = otherUser.Id, FactKey = "color", Text = "blue" };
        var otherBotFact = new Memory { BotId = otherBot.Id, SubjectId = user.Id, FactKey = "color", Text = "blue" };
        db.Memories.AddRange(memory, independent, otherAuthor, otherBotFact);
        var messages = sessions.Select((session, i) => new ChatMessage
        {
            BotId = bot.Id, ConversationId = conversation.Id, SessionId = session.Id,
            SenderId = user.Id, ExternalMessageId = "sentinel-" + i, Text = "blue; rice; piano",
            SegmentsJson = "[]", AllowReflection = true, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        }).ToArray();
        db.Messages.AddRange(messages);
        var dependent = new ChatMessage
        {
            BotId = bot.Id, ConversationId = conversation.Id, SessionId = sessions[0].Id,
            ExternalMessageId = "reply", Role = MessageRole.Assistant, Text = "old belief",
            ContextSnapshotJson = Json.Write(new ContextSnapshot(bot.Id, sessions[0].Id, 1, 1, 0, 0,
                [memory.Id], "test", Sources: [ContentSources.Memory(memory)])),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Messages.Add(dependent);
        var summaries = sessions.Select(session => new ConversationSummary
        {
            BotId = bot.Id, SessionId = session.Id, Revision = 1, Text = "independent summary",
            MemoryIdsJson = Json.Write(new[] { independent.Id }),
            SourcesJson = Json.Write(new[] { ContentSources.Memory(independent) })
        }).ToArray();
        db.Summaries.AddRange(summaries);
        var relationship = new RelationshipState { BotId = bot.Id, UserId = user.Id };
        db.Relationships.Add(relationship);
        db.RelationshipClaims.Add(new RelationshipClaim
        {
            RelationshipId = relationship.Id, BotId = bot.Id, SubjectId = user.Id,
            Kind = "Preference", Text = "piano", Origin = "Explicit"
        });
        var independentNote = new Note
            { BotId = bot.Id, UserId = user.Id, ConversationId = conversation.Id, Content = "independent note" };
        var dependentNote = new Note
        {
            BotId = bot.Id, UserId = user.Id, ConversationId = conversation.Id, Content = "retained old note",
            SourceMemoryIdsJson = Json.Write(new[] { memory.Id })
        };
        db.Notes.AddRange(independentNote, dependentNote);
        db.NoteRevisions.Add(new NoteRevision
            { NoteId = dependentNote.Id, Version = 1, Content = dependentNote.Content });
        db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id, Valence = .2 });
        var mediaPath = Path.GetFullPath(".local/memory-gate1-media-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(mediaPath, "original media sentinel");
        db.Media.Add(new MediaAsset
        {
            BotId = bot.Id, MessageId = messages[0].Id, Path = mediaPath,
            ContentType = "text/plain", Size = 23, Hash = Json.Hash("original media sentinel")
        });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<MemoryService>()
            .EditAsync(user.Id, memory.Id, "green", memory.Revision, "sentinel-edit", default);
        db.ChangeTracker.Clear();
        foreach (var message in messages)
        {
            var actual = await db.Messages.SingleAsync(x => x.Id == message.Id);
            await Assert.That(actual.Text).IsEqualTo(message.Text);
            await Assert.That(actual.SegmentsJson).IsEqualTo(message.SegmentsJson);
            await Assert.That(actual.ContextInvalidated).IsFalse();
        }

        foreach (var summary in summaries)
        {
            var actual = await db.Summaries.SingleAsync(x => x.Id == summary.Id);
            await Assert.That(actual.Text).IsEqualTo(summary.Text);
            await Assert.That(actual.ContextInvalidated).IsFalse();
        }

        await Assert.That((await db.RelationshipClaims.SingleAsync()).Text).IsEqualTo("piano");
        foreach (var fact in new[] { independent, otherAuthor, otherBotFact })
            await Assert.That((await db.Memories.SingleAsync(x => x.Id == fact.Id)).Revision).IsEqualTo(1);
        await Assert.That((await db.Notes.SingleAsync(x => x.Id == independentNote.Id)).SourceInvalidated).IsFalse();
        await Assert.That((await db.Notes.SingleAsync(x => x.Id == dependentNote.Id)).SourceInvalidated).IsTrue();
        await Assert.That((await db.Notes.SingleAsync(x => x.Id == dependentNote.Id)).Content)
            .IsEqualTo(dependentNote.Content);
        await Assert.That((await db.NoteRevisions.SingleAsync()).Content).IsEqualTo(dependentNote.Content);
        await Assert.That((await db.Emotions.SingleAsync()).Valence).IsEqualTo(.2);
        await Assert.That((await db.Messages.SingleAsync(x => x.Id == dependent.Id)).Text).IsEqualTo(dependent.Text);
        await Assert.That((await db.Messages.SingleAsync(x => x.Id == dependent.Id)).ContextInvalidated).IsTrue();
        await Assert.That(await db.Revocations.CountAsync()).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(mediaPath)).IsEqualTo("original media sentinel");
        await Assert.That(await db.Media.CountAsync()).IsEqualTo(1);
        File.Delete(mediaPath);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task EditIsAtomicRetainsAuthorizedHistoryAndArchiveDoesNotMarkOutdated(bool retain)
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.LogicalUsers.Where(x => x.Id == f.User).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.RetainMemoryHistory, retain)
            .SetProperty(x => x.AllowMemoryUpdate, false).SetProperty(x => x.AllowMemoryArchive, false));
        var service = scope.ServiceProvider.GetRequiredService<MemoryService>();
        var edit = await service.EditAsync(f.User, f.Old!.Value, "manual current fact", 1, "edit", default);
        db.ChangeTracker.Clear();
        var old = await db.Memories.SingleAsync(x => x.Id == f.Old);
        var current = await db.Memories.SingleAsync(x => x.Id == edit.ResultMemoryId);
        await Assert.That(old.IsOutdated).IsTrue();
        await Assert.That(old.Text.Length > 0).IsEqualTo(retain);
        await Assert.That(old.ArchivedAt).IsNotNull();
        await Assert.That(current.SupersedesMemoryId).IsEqualTo(old.Id);
        await Assert.That(current.IsOutdated).IsFalse();
        await Assert.That(await db.Embeddings.AnyAsync(x => x.MemoryId == old.Id)).IsFalse();
        await service.ArchiveAsync(f.User, current.Id, current.Revision, "archive", default);
        db.ChangeTracker.Clear();
        await Assert.That((await db.Memories.SingleAsync(x => x.Id == current.Id)).IsOutdated).IsFalse();
        await Assert.That(await db.Revocations.AnyAsync()).IsFalse();
    }

    [Test]
    public async Task ConcurrentEditorsHaveOneWinnerAndCommittedReceiptSurvivesHostRestart()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);

        async Task<MemoryOperationReceipt?> Edit(string text, string key)
        {
            using var scope = app.Services.CreateScope();
            try
            {
                return await scope.ServiceProvider.GetRequiredService<MemoryService>()
                    .EditAsync(f.User, f.Old!.Value, text, 1, key, default);
            }
            catch (DbUpdateConcurrencyException)
            {
                return null;
            }
        }

        var edits = await Task.WhenAll(Edit("alpha", "one"), Edit("beta", "two"));
        await Assert.That(edits.Count(x => x is not null)).IsEqualTo(1);
        var winner = Array.FindIndex(edits, x => x is not null);
        using var restarted = pg.CreateApp();
        using var scope2 = restarted.Services.CreateScope();
        var service = scope2.ServiceProvider.GetRequiredService<MemoryService>();
        var retried = await service.EditAsync(f.User, f.Old!.Value, winner == 0 ? "alpha" : "beta", 1,
            winner == 0 ? "one" : "two", default);
        await Assert.That(retried).IsEqualTo(edits[winner]);
        await Assert.That(async () =>
            {
                await service.EditAsync(f.User, f.Old!.Value, "different arguments", 1, winner == 0 ? "one" : "two",
                    default);
            })
            .ThrowsExactly<DbUpdateConcurrencyException>();
        var db = scope2.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.Memories.CountAsync()).IsEqualTo(2);
        await Assert.That(await db.MemoryOperations.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task ConfiguredProfileWithoutVectorAndFailedIndexStillRecallOnlyTheNewFact()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MemoryService>();
        var edit = await service.EditAsync(f.User, f.Old!.Value, "new truth before embedding", 1, "edit", default);
        app.ModelResponses.Enqueue(MemoryMaintenanceTests.Vector());
        var recalled = await service.RetrieveAsync(new MemoryAccess(f.Bot, f.User), "truth", default);
        await Assert.That(recalled.Select(x => x.Id)).IsEquivalentTo(new[] { edit.ResultMemoryId });
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var job = await db.Outbox.SingleAsync(x => x.Type == JobKind.Embedding);
        app.ModelResponses.Enqueue("{\"data\":[]}");
        await Assert
            .That(() => scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().EmbedAsync(job.Id, default))
            .Throws<Exception>();
        app.ModelResponses.Enqueue(MemoryMaintenanceTests.Vector());
        recalled = await service.RetrieveAsync(new MemoryAccess(f.Bot, f.User), "truth", default);
        await Assert.That(recalled.Single().Text).IsEqualTo("new truth before embedding");
        await Assert.That(recalled.Count <= 5).IsTrue();
        app.ModelResponses.Enqueue(MemoryMaintenanceTests.Vector());
        await scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().EmbedAsync(job.Id, default);
        db.ChangeTracker.Clear();
        await Assert.That((await db.Embeddings.SingleAsync(x => x.MemoryId == edit.ResultMemoryId)).SourceRevision)
            .IsEqualTo(1);
    }

    [Test]
    [Arguments("Revision")]
    [Arguments("Profile")]
    [Arguments("Lease")]
    [Arguments("Archive")]
    public async Task LateEmbeddingCannotCommitAfterItsInputsOrLeaseChange(string change)
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var lease = Guid.NewGuid();
        var job = Jobs.Add(db, f.Bot, JobKind.Embedding, new IdPayload(f.Old!.Value));
        job.Status = JobStatus.Running;
        job.LeaseId = lease;
        job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
        await db.Embeddings.ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(MemoryMaintenanceTests.Vector());
        app.BeforeModelResponse = async _ =>
        {
            using var changedScope = app.Services.CreateScope();
            var fresh = changedScope.ServiceProvider.GetRequiredService<WonderDbContext>();
            if (change == "Revision")
                await fresh.Memories.Where(x => x.Id == f.Old).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Revision, 2).SetProperty(x => x.Text, "changed body"));
            if (change == "Profile")
                await fresh.Models.Where(x => x.BotId == f.Bot && x.TaskKind == TaskKind.Embedding)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Model, "changed-profile"));
            if (change == "Lease")
                await fresh.Outbox.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
            if (change == "Archive")
                await changedScope.ServiceProvider.GetRequiredService<MemoryService>()
                    .ArchiveAsync(f.User, f.Old.Value, 1, "archive", default);
        };
        if (change == "Lease")
            await Assert
                .That(() => scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>()
                    .EmbedAsync(job.Id, default, lease)).ThrowsExactly<DbUpdateConcurrencyException>();
        else await scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().EmbedAsync(job.Id, default, lease);
        await Assert.That(await db.Embeddings.AnyAsync()).IsFalse();
    }

    [Test]
    public async Task ProjectionUsesLegalSourcesAndCasTtlNeverRewriteOriginalSummary()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var summary = new ConversationSummary
        {
            BotId = f.Bot, SessionId = f.Session, Revision = 7,
            ContextInvalidated = true, Text = "unsafe-original-do-not-send",
            MemoryIdsJson = Json.Write(new[] { f.Old!.Value })
        };
        db.Summaries.Add(summary);
        db.Models.Add(new ModelProfile
        {
            BotId = f.Bot, TaskKind = TaskKind.RollingSummary, Model = "summary",
            Endpoint = "https://mock.example/v1/",
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("mock")
        });
        await db.SaveChangesAsync();
        var projections = scope.ServiceProvider.GetRequiredService<SummaryProjections>();
        var projectionJob =
            SummaryProjections.Queue(db, summary, f.User, [f.Old.Value], [], DateTimeOffset.UtcNow.AddMinutes(2));
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<SummaryWorkflow>().RunAsync(projectionJob.Id, default);
        var fresh = await db.Summaries.AsNoTracking().SingleAsync();
        await Assert.That(fresh.Text).IsEqualTo(summary.Text);
        await Assert.That(fresh.Revision).IsEqualTo(7);
        await Assert.That(fresh.ContextInvalidated).IsTrue();
        await Assert.That(app.ModelRequests.Single()).DoesNotContain(summary.Text);
        await Assert
            .That(await ContentSources.ValidAsync(db, f.Bot, [ContentSources.Projection(fresh)], default, f.User))
            .IsTrue();
        await Assert
            .That(await SummaryProjections.ExpireAsync(db, summary.Id, 0, DateTimeOffset.UtcNow.AddDays(1), default))
            .IsEqualTo(0);
        await Assert.That(await SummaryProjections.ExpireAsync(db, summary.Id, fresh.ProjectionRevision,
            DateTimeOffset.UtcNow.AddDays(1), default)).IsEqualTo(1);
        app.BeforeModelResponse = async _ =>
        {
            await db.Summaries.Where(x => x.Id == summary.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Text, "normal-summary-update").SetProperty(x => x.Revision, 8)
                .SetProperty(x => x.SafeProjectionJson, (string?)null)
                .SetProperty(x => x.ProjectionRevision, x => x.ProjectionRevision + 1));
        };
        await Assert.That(await projections.RebuildAsync(summary.Id, f.User, 7, fresh.ProjectionRevision + 1,
            [f.Old.Value], [], DateTimeOffset.UtcNow.AddMinutes(2), default)).IsFalse();
        await Assert.That((await db.Summaries.AsNoTracking().SingleAsync()).Text).IsEqualTo("normal-summary-update");
    }

    [Test]
    public async Task ProjectionWorkerLosingItsLeaseCannotPublishLateText()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var summary = new ConversationSummary
            { BotId = f.Bot, SessionId = f.Session, Revision = 3, Text = "original body" };
        db.Summaries.Add(summary);
        db.Models.Add(new ModelProfile
        {
            BotId = f.Bot, TaskKind = TaskKind.RollingSummary, Endpoint = "https://mock.example/v1/",
            Model = "projection",
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("mock")
        });
        var job = SummaryProjections.Queue(db, summary, f.User, [f.Old!.Value], [],
            DateTimeOffset.UtcNow.AddMinutes(2));
        var lease = Guid.NewGuid();
        job.Status = JobStatus.Running;
        job.LeaseId = lease;
        job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();
        app.BeforeModelResponse = async _ =>
        {
            await db.Outbox.Where(x => x.Id == job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, Guid.NewGuid()));
        };
        await Assert.That(() =>
                scope.ServiceProvider.GetRequiredService<SummaryWorkflow>().RunAsync(job.Id, default, lease))
            .ThrowsExactly<DbUpdateConcurrencyException>();
        var actual = await db.Summaries.AsNoTracking().SingleAsync();
        await Assert.That(actual.Text).IsEqualTo(summary.Text);
        await Assert.That(actual.SafeProjectionJson).IsNull();
        await Assert.That(actual.ProjectionRevision).IsEqualTo(0);
    }

    [Test]
    public async Task ApiRequiresVersionAndIdempotencyInEveryEnvironment()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = new TestApp(environment: "MemoryLifecycleTest", postgresConnection: pg.ConnectionString);
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var login = new { email = "lifecycle@example.com", password = "A-strong-test-password!" };
        await client.PostAsJsonAsync("/api/v1/auth/register", login);
        await client.PostAsJsonAsync("/api/v1/auth/login", login);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        (await db.Users.SingleAsync(x => x.Email == login.email)).LogicalUserId = f.User;
        await db.SaveChangesAsync();
        var path = "/api/v1/me/memories/" + f.Old;
        await Assert.That((await client.PatchAsJsonAsync(path, new { text = "unversioned" })).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        await Assert
            .That((await client.PatchAsJsonAsync(path, new { text = "missing-key", expectedRevision = 1 })).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "api-edit");
        var args = new { text = "current", expectedRevision = 1 };
        var response = await client.PatchAsJsonAsync(path, args);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var receipt = await response.Content.ReadFromJsonAsync<MemoryOperationReceipt>();
        var replay = await client.PatchAsJsonAsync(path, args);
        await Assert.That(await replay.Content.ReadFromJsonAsync<MemoryOperationReceipt>()).IsEqualTo(receipt);
        await Assert
            .That((await client.PatchAsJsonAsync(path, new { text = "different", expectedRevision = 1 })).StatusCode)
            .IsEqualTo(HttpStatusCode.Conflict);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "api-forget-unconfigured");
        await Assert.That((await client.DeleteAsync(path + "?expectedRevision=1")).StatusCode)
            .IsEqualTo(HttpStatusCode.Conflict);
        using var production = new TestApp(environment: "Production", postgresConnection: pg.ConnectionString);
        using var normal = production.CreateClient();
        normal.DefaultRequestHeaders.Add("X-Wonder", "1");
        normal.DefaultRequestHeaders.Add("Idempotency-Key", "production-closed");
        await normal.PostAsJsonAsync("/api/v1/auth/login", login);
        await Assert.That((await normal.PatchAsJsonAsync(path, args)).StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var caps = await normal.GetFromJsonAsync<JsonElement>("/api/v1/me/memory-capabilities");
        await Assert.That(caps.EnumerateObject().All(x => x.Value.GetBoolean())).IsTrue();
        using var owner = app.CreateClient();
        owner.DefaultRequestHeaders.Add("X-Wonder", "1");
        owner.DefaultRequestHeaders.Add("Idempotency-Key", "owner-cannot-edit-other");
        var ownerLogin = new { email = "other-owner@example.com", password = "A-strong-test-password!" };
        await owner.PostAsJsonAsync("/api/v1/auth/register", ownerLogin);
        await owner.PostAsJsonAsync("/api/v1/auth/login", ownerLogin);
        var stranger = new LogicalUser { AllowMemory = true };
        db.LogicalUsers.Add(stranger);
        var ownerAccount = await db.Users.SingleAsync(x => x.Email == ownerLogin.email);
        ownerAccount.LogicalUserId = stranger.Id;
        (await db.Bots.SingleAsync(x => x.Id == f.Bot)).OwnerId = ownerAccount.Id;
        await db.SaveChangesAsync();
        await Assert
            .That((await owner.PatchAsJsonAsync("/api/v1/me/memories/" + receipt!.ResultMemoryId, args)).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task SourceGuardsFollowOnlyActualDependenciesAndBlockCorruptModernProvenance()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var memory = await db.Memories.SingleAsync();
        var unproven = new ChatMessage
        {
            BotId = f.Bot, SessionId = f.Session, ConversationId = f.Conversation,
            ExternalMessageId = "missing-sources", Role = MessageRole.Assistant, Text = "unproven derived text",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            ContextSnapshotJson = Json.Write(new ContextSnapshot(f.Bot, f.Session, 1, 1, 0, 0, [memory.Id], "test"))
        };
        db.Messages.Add(unproven);
        var first = new ConversationSummary
        {
            BotId = f.Bot, SessionId = f.Session, Revision = 1,
            Text = "first retained body", SourcesJson = Json.Write(new[] { ContentSources.Memory(memory) })
        };
        var session = new ConversationSession
            { BotId = f.Bot, ConversationId = f.Conversation, Status = SessionStatus.Closed };
        db.Sessions.Add(session);
        var second = new ConversationSummary
        {
            BotId = f.Bot, SessionId = session.Id, Revision = 2,
            Text = "second retained body", SourcesJson = Json.Write(new[] { ContentSources.Summary(first) })
        };
        db.Summaries.AddRange(first, second);
        await db.SaveChangesAsync();
        await Assert.That(await ContentSources.MessageValidAsync(db, unproven, default)).IsFalse();
        await Assert.That(await ContentSourceActors.ResolveAsync(db, f.Bot, [ContentSources.Message(unproven)], default))
            .IsNull();
        await Assert.That(await ContentSources.ValidAsync(db, f.Bot, [ContentSources.Summary(second)], default, f.User))
            .IsTrue();
        // ID indexes cannot substitute for versioned provenance, even when every referenced row exists.
        await db.Summaries.Where(x => x.Id == first.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.SourcesJson, (string?)null)
            .SetProperty(x => x.MemoryIdsJson, Json.Write(new[] { memory.Id })));
        await Assert.That(await ContentSources.ValidAsync(db, f.Bot, [ContentSources.Summary(second)], default, f.User))
            .IsFalse();
        await Assert.That(await ContentSourceActors.ResolveAsync(db, f.Bot, [ContentSources.Summary(second)], default))
            .IsNull();
        await Assert.That(() => ContentSources.SummaryRoots(
            new ConversationSummary { BotId = f.Bot, MemoryIdsJson = Json.Write(new[] { memory.Id }) }))
            .ThrowsExactly<InvalidOperationException>();
        await db.Summaries.Where(x => x.Id == first.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourcesJson, first.SourcesJson));
        await db.Summaries.Where(x => x.Id == second.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourcesJson, "not-json"));
        await Assert.That(await ContentSources.ValidAsync(db, f.Bot, [ContentSources.Summary(second)], default, f.User))
            .IsFalse();
        await db.Summaries.Where(x => x.Id == second.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourcesJson, second.SourcesJson));
        await scope.ServiceProvider.GetRequiredService<MemoryService>()
            .ArchiveAsync(f.User, memory.Id, 1, "archive-root", default);
        await Assert.That(await ContentSources.ValidAsync(db, f.Bot, [ContentSources.Summary(second)], default, f.User))
            .IsFalse();
        var summaries = await db.Summaries.AsNoTracking().ToListAsync();
        await Assert.That(summaries.All(x => x.ContextInvalidated)).IsTrue();
        await Assert.That(summaries.Select(x => x.Text)).IsEquivalentTo(new[] { first.Text, second.Text });
        await Assert.That(summaries.Single(x => x.Id == first.Id).Revision).IsEqualTo(1);
        await Assert.That(summaries.Single(x => x.Id == second.Id).Revision).IsEqualTo(2);
    }

    [Test]
    public async Task ChatCannotPublishAFactEditedWhileItsModelRequestWasInFlight()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.Messages.Where(x => x.Id == f.Evidence)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddDays(1)));
        db.Models.Add(new ModelProfile
        {
            BotId = f.Bot, TaskKind = TaskKind.Chat, Endpoint = "https://mock.example/v1/",
            Model = "chat",
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("mock")
        });
        db.Emotions.Add(new GlobalEmotionState { BotId = f.Bot });
        db.Identities.Add(new PersonaIdentityRevision { BotId = f.Bot, Revision = 1, Content = "test persona" });
        var job = Jobs.Add(db, f.Bot, JobKind.Chat, new IdPayload(f.Evidence), f.Conversation);
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(MemoryMaintenanceTests.Vector());
        app.BeforeModelResponse = async request =>
        {
            if (!request.Contains("messages", StringComparison.Ordinal)) return;
            await scope.ServiceProvider.GetRequiredService<MemoryService>().EditAsync(f.User, f.Old!.Value,
                "new current fact", 1, "in-flight-edit", default);
        };
        await Assert.That(() => scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(job.Id, default))
            .ThrowsExactly<DbUpdateConcurrencyException>();
        await Assert.That(await db.Messages.AnyAsync(x => x.Role == MessageRole.Assistant)).IsFalse();
        await Assert.That(await db.Batches.AnyAsync()).IsFalse();
    }

    [Test]
    [Arguments("Broken")]
    [Arguments("Cycle")]
    [Arguments("Branch")]
    public async Task AmbiguousLineageRefusesMutationWithoutPartialArchive(string fault)
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var selected = await db.Memories.SingleAsync();
        if (fault == "Broken") selected.SupersedesMemoryId = Guid.NewGuid();
        if (fault == "Cycle") selected.SupersedesMemoryId = selected.Id;
        if (fault == "Branch")
            db.Memories.Add(new Memory
                { BotId = f.Bot, SubjectId = f.User, Text = "unproven successor", SupersedesMemoryId = selected.Id });
        await db.SaveChangesAsync();
        await Assert.That(async () =>
            {
                await scope.ServiceProvider.GetRequiredService<MemoryService>()
                    .EditAsync(f.User, selected.Id, "new", 1, "conflict", default);
            })
            .ThrowsExactly<DbUpdateConcurrencyException>();
        await Assert.That((await db.Memories.AsNoTracking().SingleAsync(x => x.Id == selected.Id)).ArchivedAt).IsNull();
        await Assert.That(await db.MemoryOperations.CountAsync()).IsEqualTo(0);
    }

}
