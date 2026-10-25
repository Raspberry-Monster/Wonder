using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;

namespace Wonder.IntegrationTests;

public sealed class MemoryMaintenanceTests
{
    [Test]
    public async Task CandidateIsIsolatedUntilAdmissionAndReusesItsEmbedding()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app, false);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.Memories.CountAsync()).IsEqualTo(0);
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Status).IsEqualTo(MemoryMaintenanceStatus.Pending);
        await Assert.That(await scope.ServiceProvider.GetRequiredService<MemoryService>()
            .RetrieveAsync(new MemoryAccess(f.Bot, f.User), "住哪里", default)).IsEmpty();
        app.ModelResponses.Enqueue(Vector());
        await RunAsync(app, f.Job);
        await RunAsync(app, f.Job); // Retrying a committed job cannot duplicate either evidence or jobs.
        db.ChangeTracker.Clear();
        var memory = await db.Memories.SingleAsync();
        await Assert.That(memory.Text).IsEqualTo("我现在住上海。");
        await Assert.That(memory.Status).IsEqualTo(MemoryStatus.Active);
        await Assert.That(await db.Embeddings.CountAsync()).IsEqualTo(1);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(1);
        await Assert.That(await db.Outbox.CountAsync(x => x.Type == JobKind.Embedding)).IsEqualTo(0);
    }

    [Test]
    [Arguments(MemoryMaintenanceAction.Merge)]
    [Arguments(MemoryMaintenanceAction.Supplement)]
    [Arguments(MemoryMaintenanceAction.Update)]
    [Arguments(MemoryMaintenanceAction.Archive)]
    [Arguments(MemoryMaintenanceAction.Defer)]
    public async Task SemanticDecisionsMaintainPrivateVersionsEvidenceAndDerivedContext(MemoryMaintenanceAction action)
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<MemoryService>();
        // A reply using the old fact must stop contributing to future context after a replacement.
        db.Messages.Add(new ChatMessage
        {
            BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session,
            ExternalMessageId = "reply", Role = MessageRole.Assistant, Text = "你住杭州。",
            ContextSnapshotJson =
                Json.Write(new ContextSnapshot(f.Bot, f.Session, 1, 0, null, 0, 0, [f.Old!.Value], "model"))
        });
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(action, action == MemoryMaintenanceAction.Defer ? null : f.Old,
            action == MemoryMaintenanceAction.Supplement ? "我住上海，之前住杭州。" : "我现在住上海。"));
        if (action == MemoryMaintenanceAction.Supplement) app.ModelResponses.Enqueue(Vector());
        await RunAsync(app, f.Job);
        db.ChangeTracker.Clear();
        var candidate = await db.MemoryCandidates.SingleAsync();
        await Assert.That(candidate.Action).IsEqualTo(action);
        var old = await db.Memories.SingleAsync(x => x.Id == f.Old);
        if (action is MemoryMaintenanceAction.Merge or MemoryMaintenanceAction.Defer)
        {
            await Assert.That(old.ArchivedAt).IsNull();
            await Assert.That(await db.Memories.CountAsync()).IsEqualTo(1);
            if (action == MemoryMaintenanceAction.Merge)
                await Assert.That(Json.Read<Guid[]>(old.EvidenceJson).Contains(f.Evidence)).IsTrue();
        }
        else
        {
            await Assert.That(old.ArchivedAt).IsNotNull();
            await Assert.That((await db.Messages.SingleAsync(x => x.ExternalMessageId == "reply")).ContextInvalidated)
                .IsTrue();
            await Assert.That(await db.Embeddings.AnyAsync(x => x.MemoryId == old.Id)).IsFalse();
            await Assert.That(await MemoryQueries.Authorized(db, new MemoryAccess(f.Bot, f.User))
                .AnyAsync(x => x.Id == old.Id)).IsFalse();
            if (action != MemoryMaintenanceAction.Archive)
            {
                var current = await db.Memories.SingleAsync(x => x.ArchivedAt == null);
                await Assert.That(current.SupersedesMemoryId).IsEqualTo(old.Id);
                await Assert.That(await db.Embeddings.AnyAsync(x => x.MemoryId == current.Id)).IsTrue();
            }
        }

        using var request = JsonDocument.Parse(app.ModelRequests[1]);
        var data = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        await Assert.That(data).Contains(f.Old!.Value.ToString());
        await Assert.That(data).Contains(f.Evidence.ToString());
    }

    [Test]
    public async Task IdenticalTextMergesWithoutProviderCallsEvenWithDifferentFactKeys()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app, text: "我住杭州。");
        await RunAsync(app, f.Job);
        await RunAsync(app, f.Job);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var memory = await db.Memories.SingleAsync();
        await Assert.That(memory.FactKey).IsEqualTo("old-home-key");
        await Assert.That(Json.Read<Guid[]>(memory.EvidenceJson)).HasSingleItem();
        await Assert.That(app.ModelRequests).IsEmpty();
    }

    [Test]
    [Arguments(MemoryMaintenanceAction.Merge)]
    [Arguments(MemoryMaintenanceAction.Update)]
    [Arguments(MemoryMaintenanceAction.Supplement)]
    [Arguments(MemoryMaintenanceAction.Archive)]
    public async Task ServerEnforcesDisabledOperationsDespiteModelDecision(MemoryMaintenanceAction action)
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var user = await db.LogicalUsers.SingleAsync();
        user.AllowMemoryMerge = user.AllowMemoryUpdate = user.AllowMemoryArchive = false;
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(action, f.Old, "我现在住上海。"));
        await RunAsync(app, f.Job);
        db.ChangeTracker.Clear();
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Status).IsEqualTo(MemoryMaintenanceStatus.Deferred);
        await Assert.That((await db.Memories.SingleAsync()).Text).IsEqualTo("我住杭州。");
        await Assert.That((await db.Memories.SingleAsync()).ArchivedAt).IsNull();
    }

    [Test]
    public async Task VectorRecallCannotCrossAuthorOrScopeAndForeignTargetsAreRejected()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var other = new LogicalUser { AllowMemory = true };
        db.LogicalUsers.Add(other);
        var foreign = new Memory
        {
            BotId = f.Bot, SubjectId = other.Id, Text = "FOREIGN_AUTHOR_SECRET", FactKey = "new-home-key", Status = MemoryStatus.Active
        };
        var foreignBot = new BotInstance { OwnerId = (await db.Bots.SingleAsync(x => x.Id == f.Bot)).OwnerId };
        db.Bots.Add(foreignBot);
        var group = new Memory
        {
            BotId = foreignBot.Id, SubjectId = f.User,
            Text = "FOREIGN_SCOPE_SECRET", FactKey = "new-home-key",
            Status = MemoryStatus.Active
        };
        db.Memories.AddRange(foreign, group);
        var profile = await db.EmbeddingProfiles.SingleAsync();
        db.Embeddings.AddRange(
            new MemoryEmbedding
                { MemoryId = foreign.Id, ProfileId = profile.Id, Values = new Vector(new float[] { 1, 0, 0 }) },
            new MemoryEmbedding
                { MemoryId = group.Id, ProfileId = profile.Id, Values = new Vector(new float[] { 1, 0, 0 }) });
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(MemoryMaintenanceAction.Merge, foreign.Id));
        await Assert.That(() => RunAsync(app, f.Job)).ThrowsExactly<JsonException>();
        await Assert.That(app.ModelRequests[1]).DoesNotContain("FOREIGN_AUTHOR_SECRET");
        await Assert.That(app.ModelRequests[1]).DoesNotContain("FOREIGN_SCOPE_SECRET");
        await Assert.That(await db.Memories.CountAsync()).IsEqualTo(3);
    }

    [Test]
    public async Task ExplicitDeletionPurgesEveryVersionAuditAndPendingCandidate()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(MemoryMaintenanceAction.Update, f.Old, "我现在住上海。"));
        await RunAsync(app, f.Job);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var latest = await db.Memories.SingleAsync(x => x.ArchivedAt == null);
        await scope.ServiceProvider.GetRequiredService<MemoryService>().RevokeAsync(f.User, latest.Id, null, default);
        await RunAsync(app, f.Job);
        db.ChangeTracker.Clear();
        await Assert.That(await db.Memories.AnyAsync(x => x.Text != "" || x.Status != MemoryStatus.Revoked)).IsFalse();
        await Assert.That(await db.Embeddings.AnyAsync()).IsFalse();
        await Assert
            .That(await db.MemoryCandidates.AnyAsync(x => x.Text != "" || x.Reason != "" || x.EvidenceJson != "[]"))
            .IsFalse();
    }

    [Test]
    public async Task NoHistoryPolicyErasesArchivedTextAndReenablingDoesNotRestoreIt()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var user = await db.LogicalUsers.SingleAsync();
        user.RetainMemoryHistory = false;
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(MemoryMaintenanceAction.Update, f.Old, "我现在住上海。"));
        await RunAsync(app, f.Job);
        db.ChangeTracker.Clear();
        var old = await db.Memories.SingleAsync(x => x.Id == f.Old);
        await Assert.That(old.Text).IsEqualTo("");
        await Assert.That(old.EvidenceJson).IsEqualTo("[]");
        await Assert.That((await db.Memories.SingleAsync(x => x.ArchivedAt == null)).Text).IsEqualTo("我现在住上海。");
        var service = scope.ServiceProvider.GetRequiredService<MemoryService>();
        await service.UpdateSettingsAsync(f.User, true, null, null, null, null, true, default);
        db.ChangeTracker.Clear();
        await Assert.That((await db.Memories.SingleAsync(x => x.Id == f.Old)).Text).IsEqualTo("");
        var latest = await db.Memories.SingleAsync(x => x.ArchivedAt == null);
        await service.RevokeAsync(f.User, latest.Id, null, default);
        await Assert.That(await db.Revocations.AnyAsync(x => x.FactKeyHash == Json.Hash("old-home-key"))).IsTrue();
        await Assert.That(await db.Revocations.AnyAsync(x => x.FactKeyHash == Json.Hash("new-home-key"))).IsTrue();
    }

    [Test]
    public async Task InvalidEmbeddingDoesNotArchiveOrPartiallyRewriteFacts()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        app.ModelResponses.Enqueue("""{"data":[{"index":0,"embedding":[0,0,0]}]}""");
        await Assert.That(() => RunAsync(app, f.Job)).ThrowsExactly<ModelResponseException>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That((await db.Memories.SingleAsync()).Text).IsEqualTo("我住杭州。");
        await Assert.That((await db.Memories.SingleAsync()).ArchivedAt).IsNull();
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Status).IsEqualTo(MemoryMaintenanceStatus.Pending);
    }

    [Test]
    public async Task TruncatedMaintenanceResponsePreservesFactsAndRecordsUsage()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(
            """{"choices":[{"finish_reason":"length","message":{"content":"{}"}}],"usage":{"prompt_tokens":20,"completion_tokens":1024}}""");
        await Assert.That(() => RunAsync(app, f.Job)).ThrowsExactly<OutputLimitException>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That((await db.Memories.SingleAsync()).ArchivedAt).IsNull();
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Status).IsEqualTo(MemoryMaintenanceStatus.Pending);
        await Assert.That(await db.Usage.CountAsync()).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeletionOrPolicyChangeDuringProviderCallPreventsCommit(bool policyChange)
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(MemoryMaintenanceAction.Update, f.Old, "我现在住上海。"));
        app.BeforeModelResponse = async body =>
        {
            if (!body.Contains("messages")) return;
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<MemoryService>();
            if (policyChange) await service.UpdateSettingsAsync(f.User, false, null, default);
            else await service.RevokeAsync(f.User, f.Old!.Value, null, default);
        };
        await RunAsync(app, f.Job);
        using var verify = app.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.Memories.AnyAsync(x => x.Text == "我现在住上海。")).IsFalse();
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Status)
            .IsEqualTo(MemoryMaintenanceStatus.Cancelled);
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Text).IsEqualTo("");
    }

    [Test]
    public async Task ConcurrentNewFactForcesReevaluationInsteadOfDuplicateAdmission()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app, false);
        app.ModelResponses.Enqueue(Vector());
        app.BeforeModelResponse = async _ =>
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            db.Memories.Add(new Memory
            {
                BotId = f.Bot, SubjectId = f.User, Text = "我现在住上海。", FactKey = "concurrently-added", Status = MemoryStatus.Active
            });
            await db.SaveChangesAsync();
        };
        await Assert.That(() => RunAsync(app, f.Job)).ThrowsExactly<DbUpdateConcurrencyException>();
        app.BeforeModelResponse = null;
        await RunAsync(app, f.Job); // Re-evaluation now finds the exact text and merges.
        using var verify = app.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.Memories.CountAsync()).IsEqualTo(1);
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Action).IsEqualTo(MemoryMaintenanceAction.Merge);
    }

    [Test]
    public async Task OlderEvidenceCannotOverwriteANewerCurrentFact()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        (await db.Memories.SingleAsync()).LastConfirmedAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(MemoryMaintenanceAction.Update, f.Old, "我现在住上海。"));
        await RunAsync(app, f.Job);
        db.ChangeTracker.Clear();
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Status).IsEqualTo(MemoryMaintenanceStatus.Deferred);
        await Assert.That((await db.Memories.SingleAsync()).ArchivedAt).IsNull();
    }

    [Test]
    public async Task ChangedEmbeddingProfileBackfillsBeforeMakingSemanticDecisions()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        (await db.Models.SingleAsync(x => x.TaskKind == TaskKind.Embedding)).Model = "new-embedding-model";
        await db.SaveChangesAsync();
        await RunAsync(app, f.Job);
        await Assert.That(app.ModelRequests).IsEmpty();
        await Assert.That((await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == f.Job)).Status)
            .IsEqualTo(JobStatus.Pending);
        var backfill = await db.Outbox.SingleAsync(x => x.Type == JobKind.Embedding);
        app.ModelResponses.Enqueue(Vector());
        await scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().EmbedAsync(backfill.Id, default);
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(MemoryMaintenanceAction.Merge, f.Old));
        await RunAsync(app, f.Job);
        db.ChangeTracker.Clear();
        await Assert.That(await db.EmbeddingProfiles.CountAsync()).IsEqualTo(2);
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Action).IsEqualTo(MemoryMaintenanceAction.Merge);
    }

    [Test]
    public async Task OwnPolicyHistoryAndAuditAreAvailableWithoutGrantingOwnerAccess()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await SeedAsync(app);

        async Task<HttpClient> Login(string name, Guid user)
        {
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Add("X-Wonder", "1");
            var login = new { email = name + "@example.com", password = "A-strong-test-password!" };
            await client.PostAsJsonAsync("/api/v1/auth/register", login);
            await client.PostAsJsonAsync("/api/v1/auth/login", login);
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            (await db.Users.SingleAsync(x => x.Email == login.email)).LogicalUserId = user;
            await db.SaveChangesAsync();
            return client;
        }

        using var own = await Login("self", f.User);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var other = new LogicalUser();
        db.LogicalUsers.Add(other);
        await db.SaveChangesAsync();
        using var stranger = await Login("stranger", other.Id);
        (await db.Bots.SingleAsync()).OwnerId = (await db.Users.SingleAsync(x => x.Email == "stranger@example.com")).Id;
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(Vector());
        app.ModelResponses.Enqueue(Decision(MemoryMaintenanceAction.Update, f.Old, "我现在住上海。"));
        await RunAsync(app, f.Job);
        await Assert.That((await own.GetFromJsonAsync<JsonElement>("/api/v1/me/memories")).GetArrayLength())
            .IsEqualTo(1);
        await Assert.That((await own.GetFromJsonAsync<JsonElement>("/api/v1/me/memories?includeArchived=true"))
            .GetArrayLength()).IsEqualTo(2);
        await Assert.That((await own.GetFromJsonAsync<JsonElement>("/api/v1/me/memory-maintenance")).GetArrayLength())
            .IsEqualTo(1);
        await Assert
            .That((await stranger.GetFromJsonAsync<JsonElement>("/api/v1/me/memory-maintenance")).GetArrayLength())
            .IsEqualTo(0);
        await Assert.That((await own.PutAsJsonAsync("/api/v1/me/memory-settings", new
            {
                allowMemory = true,
                allowMemoryMerge = false, allowMemoryUpdate = false, allowMemoryArchive = false,
                retainMemoryHistory = false
            })).StatusCode)
            .IsEqualTo(HttpStatusCode.NoContent);
        var settings = await own.GetFromJsonAsync<JsonElement>("/api/v1/me/memory-settings");
        await Assert.That(settings.GetProperty("allowMemoryUpdate").GetBoolean()).IsFalse();
        await Assert.That(settings.GetProperty("retainMemoryHistory").GetBoolean()).IsFalse();
        await Assert.That((await own.GetFromJsonAsync<JsonElement>("/api/v1/me/memories?includeArchived=true"))
            .GetArrayLength()).IsEqualTo(1);
    }

    internal static async Task RunAsync(TestApp app, Guid job)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MemoryMaintenanceWorkflow>().RunAsync(job, default);
    }

    internal static string Vector()
    {
        return """{"data":[{"index":0,"embedding":[1,0,0]}]}""";
    }

    internal static string Decision(MemoryMaintenanceAction action, Guid? target, string? text = null)
    {
        return Json.Write(new
        {
            choices = new[]
            {
                new
                {
                    finish_reason = "stop", message = new
                    {
                        content = Json.Write(new MemoryMaintenanceDecision(action, target, text,
                            "Based on explicit evidence."))
                    }
                }
            },
            usage = new { prompt_tokens = 20, completion_tokens = 20 }
        });
    }

    internal static async Task<Fixture> SeedAsync(TestApp app, bool existing = true, string text = "我现在住上海。")
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        var user = new LogicalUser { AllowMemory = true };
        var conversation = new Conversation { BotId = bot.Id };
        var session = new ConversationSession { BotId = bot.Id, ConversationId = conversation.Id };
        var message = new ChatMessage
        {
            BotId = bot.Id, ConversationId = conversation.Id, SessionId = session.Id,
            SenderId = user.Id, AllowReflection = true, ExternalMessageId = "source", Text = text,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        db.Users.Add(owner);
        db.Bots.Add(bot);
        db.LogicalUsers.Add(user);
        db.Conversations.Add(conversation);
        db.Sessions.Add(session);
        db.Messages.Add(message);
        var secret = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key");
        foreach (var kind in new[] { TaskKind.Embedding, TaskKind.StrongReflection })
            db.Models.Add(new ModelProfile
            {
                BotId = bot.Id, TaskKind = kind, Endpoint = "https://model.example/v1/",
                Model = kind.ToString(), EmbeddingDimensions = 3, ProtectedApiKey = secret, SupportsJson = true
            });
        Memory? memory = null;
        if (existing)
        {
            memory = new Memory
            {
                BotId = bot.Id, SubjectId = user.Id, FactKey = "old-home-key", Text = "我住杭州。", Status = MemoryStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2)
            };
            var profile = new EmbeddingProfile
                { BotId = bot.Id, Provider = "https://model.example/v1/", Model = "Embedding", Dimensions = 3 };
            db.Memories.Add(memory);
            db.EmbeddingProfiles.Add(profile);
            db.Embeddings.Add(new MemoryEmbedding
                { MemoryId = memory.Id, ProfileId = profile.Id, Values = new Vector(new float[] { 1, 0, 0 }) });
        }

        await db.SaveChangesAsync();
        var proposal = new MemoryCandidate("new-home-key", text, true, false, [message.Id]);
        var reflection = new ReflectionResult
        {
            BotId = bot.Id, SubjectId = user.Id, SessionId = session.Id,
            Boundary = message.Sequence, RevocationEpoch = user.RevocationEpoch,
            Payload = Json.Write(new ReflectionProposal(false, null, [], [proposal, proposal]))
        };
        db.Reflections.Add(reflection);
        var job = Jobs.Add(db, bot.Id, JobKind.Memory, new IdPayload(reflection.Id));
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().ApplyAsync(job.Id, default);
        await scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().ApplyAsync(job.Id, default);
        var maintenance = await db.Outbox.SingleAsync(x => x.Type == JobKind.MemoryMaintenance);
        return new Fixture(bot.Id, user.Id, conversation.Id, session.Id, message.Id, memory?.Id, maintenance.Id);
    }

    internal sealed record Fixture(
        Guid Bot,
        Guid User,
        Guid Conversation,
        Guid Session,
        Guid Evidence,
        Guid? Old,
        Guid Job);
}
