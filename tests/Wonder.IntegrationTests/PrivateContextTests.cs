using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class PrivateContextTests
{
    [Test]
    public async Task RemovedSharingAndSpaceRoutesCannotBeUsed()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        await Assert.That((await f.Alice.GetAsync($"/api/v1/me/memories/{f.SourceId}/sharing-options")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await f.Alice.PostAsJsonAsync($"/api/v1/me/memories/{f.SourceId}/sharing", new { scope = "UserShared" })).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await f.Owner.GetAsync($"/api/v1/bots/{f.BotId}/context-spaces")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await f.Alice.GetAsync($"/api/v1/me/memories/{f.PersonalId}")).StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task GroupRepliesRecognizeOnlyTheCurrentSpeakerWithoutPersonalContext()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        await EnableAsync(f.Alice, "小明");
        await f.Bob.PutAsJsonAsync("/api/v1/me/memory-settings", new { allowMemory = false, preferredName = "小李" });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();
        var workflow = scope.ServiceProvider.GetRequiredService<ChatWorkflow>();

        async Task Send(string id, string sender, string group, string text)
        {
            await ingestor.IngestAsync(f.BindingId, new NormalizedEvent("999", id, sender, group,
                ConversationKind.Group,
                [new MessageSegment(SegmentKind.Mention, "999"), new MessageSegment(SegmentKind.Text, text)],
                DateTimeOffset.UtcNow), default);
            var message = await db.Messages.AsNoTracking()
                .SingleAsync(x => x.BotId == f.BotId && x.ExternalMessageId == id);
            var job = await db.Outbox.AsNoTracking().SingleAsync(x =>
                x.BotId == f.BotId && x.Type == JobKind.Chat && x.Payload.Contains(message.Id.ToString()));
            await workflow.RunAsync(job.Id, default);
        }

        await Send("bob-1", "22222", "10002", "继续讨论部署");
        using var request = JsonDocument.Parse(app.ModelRequests[0]);
        var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var current = messages[^1].GetProperty("content").GetString()!;
        await Assert.That(current).Contains("\"小李\"");
        await Assert.That(current).DoesNotContain("小明");
        await Assert.That(current).DoesNotContain("树莓");
        var reference = messages[1].GetProperty("content").GetString()!;
        var conversation = await db.Conversations.AsNoTracking()
            .SingleAsync(x => x.BotId == f.BotId && x.ExternalId == "10002");
        await Assert.That(reference).DoesNotContain(SpeakerTags.For(conversation.Id, f.AliceId));
        await Assert.That(reference).DoesNotContain("scope=SpaceShared");
        await Assert.That(reference).DoesNotContain("偏好简洁");
        await Send("alice-1", "11111", "10003", "请简短些");
        using var otherRequest = JsonDocument.Parse(app.ModelRequests[1]);
        var other = otherRequest.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        await Assert.That(other[^1].GetProperty("content").GetString()!).Contains("\"小明\"");
        await Assert.That(other[1].GetProperty("content").GetString()!).DoesNotContain("scope=UserShared");
        await Assert.That(app.ModelRequests[1]).DoesNotContain("继续讨论部署");
        await Assert.That(app.ModelRequests[1]).DoesNotContain(f.AliceId.ToString());
        await Assert.That(app.ModelRequests[1]).DoesNotContain(f.BobId.ToString());
    }

    [Test]
    [Arguments("", "[]", true, false)]
    [Arguments("  ", "[]", true, false)]
    [Arguments("not-json", "[]", true, false)]
    [Arguments("null", "[]", true, false)]
    [Arguments("{}", "[]", true, false)]
    [Arguments("{\"retrievedMemoryIds\":[]}", "", false, true)]
    [Arguments("{\"retrievedMemoryIds\":[]}", "null", false, true)]
    [Arguments("{\"retrievedMemoryIds\":[]}", "[\"invalid-guid\"]", false, true)]
    public async Task CorrectionDoesNotWidenInvalidationForUnprovenCaches(string snapshot, string summaryIds,
        bool invalidSnapshot, bool invalidSummary)
    {
        using var app = new TestApp(environment: "MemoryLifecycleTest");
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var conversation = new Conversation
            { BotId = f.BotId, Kind = ConversationKind.Group, ExternalId = "legacy-cache" };
        var session = new ConversationSession { BotId = f.BotId, ConversationId = conversation.Id };
        db.Conversations.Add(conversation);
        db.Sessions.Add(session);
        var job = Jobs.Add(db, f.BotId, JobKind.Chat, new IdPayload(Guid.NewGuid()), conversation.Id);
        var message = new ChatMessage
        {
            BotId = f.BotId, ConversationId = conversation.Id, SessionId = session.Id,
            Role = MessageRole.Assistant, ExternalMessageId = "generated:" + job.Id, Text = "legacy cached answer",
            ContextSnapshotJson = snapshot, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        var summary = new ConversationSummary
        {
            BotId = f.BotId, SessionId = session.Id,
            Text = "legacy summary", MemoryIdsJson = summaryIds
        };
        var batch = new OutgoingMessageBatch
            { BotId = f.BotId, ConversationId = conversation.Id, SessionId = session.Id, SourceJobId = job.Id };
        var part = new OutgoingMessagePart { BatchId = batch.Id, Text = message.Text };
        db.Messages.Add(message);
        db.Summaries.Add(summary);
        db.Batches.Add(batch);
        db.Parts.Add(part);
        await db.SaveChangesAsync();

        f.Alice.DefaultRequestHeaders.Add("Idempotency-Key", "cache-correction");
        var response = await f.Alice.PatchAsJsonAsync($"/api/v1/me/memories/{f.SourceId}", new { text = "修正后的项目状态", expectedRevision = 1 });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        db.ChangeTracker.Clear();
        var oldMemory = await db.Memories.SingleAsync(x => x.Id == f.SourceId);
        await Assert.That(oldMemory.Status == MemoryStatus.Revoked).IsFalse();
        await Assert.That(oldMemory.IsOutdated).IsTrue();
        await Assert.That(await db.Memories.Where(x => x.SubjectId == f.AliceId && x.Text == "修正后的项目状态" &&
                                                       x.Status != MemoryStatus.Revoked).ToListAsync()).HasSingleItem();
        var cachedMessage = await db.Messages.SingleAsync(x => x.Id == message.Id);
        await Assert.That(cachedMessage.ContextInvalidated).IsFalse();
        if (invalidSnapshot) await Assert.That(await ContentSources.MessageValidAsync(db, cachedMessage, default)).IsFalse();
        await Assert.That(cachedMessage.Text).IsEqualTo("legacy cached answer");
        var cachedSummary = await db.Summaries.SingleAsync(x => x.Id == summary.Id);
        await Assert.That(cachedSummary.ContextInvalidated).IsFalse();
        if (invalidSummary || summaryIds == "[]") await Assert.That(await ContentSources.ValidAsync(db, f.BotId, [ContentSources.Summary(cachedSummary)], default)).IsFalse();
        await Assert.That(cachedSummary.Text).IsEqualTo("legacy summary");
        await Assert.That((await db.Parts.SingleAsync(x => x.Id == part.Id)).Status)
            .IsEqualTo(DeliveryStatus.Pending);
    }

    [Test]
    public async Task MaintenanceSweepPreservesPendingWorkAndRemovesExpiredData()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var conversation = new Conversation
            { BotId = f.BotId, ExternalId = "maintenance", Kind = ConversationKind.Group };
        var pendingSession = new ConversationSession
            { BotId = f.BotId, ConversationId = conversation.Id, Status = SessionStatus.Reflecting };
        var doneSession = new ConversationSession
            { BotId = f.BotId, ConversationId = conversation.Id, Status = SessionStatus.Reflecting };
        db.Conversations.Add(conversation);
        db.Sessions.AddRange(pendingSession, doneSession);
        Jobs.Add(db, f.BotId, JobKind.Reflection,
            new ReflectionJob(pendingSession.Id, f.AliceId, 0, 0, ReflectionKind.Full), conversation.Id);
        var expired = new ChatMessage
        {
            BotId = f.BotId, ConversationId = conversation.Id, SessionId = doneSession.Id,
            ExternalMessageId = "expired", ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var retained = new ChatMessage
        {
            BotId = f.BotId, ConversationId = conversation.Id, SessionId = pendingSession.Id,
            ExternalMessageId = "retained", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Messages.AddRange(expired, retained);
        var expiredMedia = new MediaAsset { BotId = f.BotId, MessageId = expired.Id, ExpiresAt = expired.ExpiresAt };
        var retainedMedia = new MediaAsset { BotId = f.BotId, MessageId = retained.Id, ExpiresAt = retained.ExpiresAt };
        db.Media.AddRange(expiredMedia, retainedMedia);

        OutgoingMessageBatch Batch(DeliveryStatus status, int age)
        {
            var job = Jobs.Add(db, f.BotId, JobKind.Chat, new IdPayload(Guid.NewGuid()), conversation.Id);
            job.Status = JobStatus.Completed;
            job.CreatedAt = DateTimeOffset.UtcNow.AddDays(-age);
            var batch = new OutgoingMessageBatch
                { BotId = f.BotId, ConversationId = conversation.Id, SessionId = doneSession.Id, SourceJobId = job.Id };
            db.Batches.Add(batch);
            db.Parts.Add(new OutgoingMessagePart { BatchId = batch.Id, Text = "delivery", Status = status });
            return batch;
        }

        var expiredBatch = Batch(DeliveryStatus.Sent, 31);
        var pendingBatch = Batch(DeliveryStatus.Pending, 31);
        var recentBatch = Batch(DeliveryStatus.Sent, 1);
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().SweepSessionsAsync(default);
        await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().ScheduleDailyAsync(default);
        await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().CleanupAsync(default);
        db.ChangeTracker.Clear();
        await Assert.That(await db.Messages.AnyAsync(x => x.Id == expired.Id)).IsFalse();
        await Assert.That(await db.Messages.AnyAsync(x => x.Id == retained.Id)).IsTrue();
        await Assert.That(await db.Media.AnyAsync(x => x.Id == expiredMedia.Id)).IsFalse();
        await Assert.That(await db.Media.AnyAsync(x => x.Id == retainedMedia.Id)).IsTrue();
        await Assert.That(await db.Batches.AnyAsync(x => x.Id == expiredBatch.Id)).IsFalse();
        await Assert.That(await db.Parts.AnyAsync(x => x.BatchId == expiredBatch.Id)).IsFalse();
        await Assert.That(await db.Batches.AnyAsync(x => x.Id == pendingBatch.Id)).IsTrue();
        await Assert.That(await db.Batches.AnyAsync(x => x.Id == recentBatch.Id)).IsTrue();
        await Assert.That((await db.Sessions.SingleAsync(x => x.Id == pendingSession.Id)).Status)
            .IsEqualTo(SessionStatus.Reflecting);
        await Assert.That((await db.Sessions.SingleAsync(x => x.Id == doneSession.Id)).Status)
            .IsEqualTo(SessionStatus.Closed);
    }

    private static async Task EnableAsync(HttpClient client, string name)
    {
        var response = await client.PutAsJsonAsync("/api/v1/me/memory-settings", new { allowMemory = true, preferredName = name });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private static async Task<(HttpClient Client, Guid Id)> AccountAsync(TestApp app, string name)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var input = new { email = name + "@example.com", password = "A-strong-test-password!" };
        await client.PostAsJsonAsync("/api/v1/auth/register", input);
        await client.PostAsJsonAsync("/api/v1/auth/login", input);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return (client, me.GetProperty("id").GetGuid());
    }

    private static async Task<Fixture> SeedAsync(TestApp app)
    {
        var owner = await AccountAsync(app, "owner");
        var alice = await AccountAsync(app, "alice");
        var bob = await AccountAsync(app, "bob");
        var created = await owner.Client.PostAsJsonAsync("/api/v1/bots", new { name = "Tree", identity = "你的名字是树莓，你是助手。" });
        var bot = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var a = new LogicalUser { AllowMemory = true };
        var b = new LogicalUser { AllowMemory = true };
        db.LogicalUsers.AddRange(a, b);
        (await db.Users.SingleAsync(x => x.Id == alice.Id)).LogicalUserId = a.Id;
        (await db.Users.SingleAsync(x => x.Id == bob.Id)).LogicalUserId = b.Id;
        db.PlatformIdentities.AddRange(new PlatformIdentity { UserId = a.Id, ExternalId = "11111" }, new PlatformIdentity { UserId = b.Id, ExternalId = "22222" });
        db.GroupPolicies.AddRange(new GroupPolicy { BotId = bot, GroupId = "10002" }, new GroupPolicy { BotId = bot, GroupId = "10003" });
        var binding = new PlatformBinding { BotId = bot, PlatformAccountId = "999" };
        db.Bindings.Add(binding);
        db.Models.Add(new ModelProfile { BotId = bot, TaskKind = TaskKind.Chat, Endpoint = "https://model.example/v1/", Model = "test", ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key") });
        var source = new Memory { BotId = bot, SubjectId = a.Id, FactKey = "project-progress", Text = "项目已完成测试，下一步部署。", Status = MemoryStatus.Active };
        var personal = new Memory { BotId = bot, SubjectId = a.Id, FactKey = "answer-style", Text = "偏好简洁回答。", Status = MemoryStatus.Active };
        db.Memories.AddRange(source, personal);
        await db.SaveChangesAsync();
        return new Fixture(owner.Client, alice.Client, bob.Client, bot, a.Id, b.Id, source.Id, personal.Id, binding.Id);
    }

    private sealed record Fixture(HttpClient Owner, HttpClient Alice, HttpClient Bob, Guid BotId, Guid AliceId, Guid BobId, Guid SourceId, Guid PersonalId, Guid BindingId);
}
