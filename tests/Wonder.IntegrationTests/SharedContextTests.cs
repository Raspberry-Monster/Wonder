using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class SharedContextTests
{
    [Test]
    public async Task PersonalSharingIsExplicitAndLimitedToTheSameUserAndBot()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var url = $"/api/v1/me/memories/{f.PersonalId}/sharing";
        await Assert.That((await f.Alice.PostAsJsonAsync(url, new { scope = "UserShared" })).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await f.Owner.PostAsJsonAsync(url, new { scope = "UserShared" })).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await EnableAsync(f.Alice, "小明");
        var shared = await ShareAsync(f.Alice, f.PersonalId, "UserShared");
        await Assert.That(await ShareAsync(f.Alice, f.PersonalId, "UserShared")).IsEqualTo(shared);
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MemoryService>();
        foreach (var group in new[] { "10002", "10003", "10004" })
            await Assert.That((await service.RetrieveAsync(new(f.BotId, f.AliceId, group), "偏好", default)).Any(x => x.Id == shared)).IsTrue();
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.BobId, "10002"), "偏好", default)).Any(x => x.Id == shared)).IsFalse();
        await Assert.That((await service.RetrieveAsync(new(Guid.NewGuid(), f.AliceId, "10002"), "偏好", default))).IsEmpty();
        await f.Alice.PutAsJsonAsync("/api/v1/me/memory-settings", new { allowMemory = true, allowCrossGroupMemory = false });
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.AliceId, "10002"), "偏好", default)).Any(x => x.Id == shared)).IsFalse();
        await EnableAsync(f.Alice, "小明");
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.AliceId, "10002"), "偏好", default)).Any(x => x.Id == shared)).IsFalse();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That((await db.Memories.AsNoTracking().SingleAsync(x => x.Id == f.PersonalId)).Status).IsNotEqualTo(MemoryStatus.Revoked);
        var list = await f.Alice.GetFromJsonAsync<JsonElement>("/api/v1/me/memories");
        await Assert.That(list.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == f.PersonalId).GetProperty("sharing").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task SharedSpaceRespectsSourceMembershipAndOtherGroupsRemainSeparate()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        await EnableAsync(f.Alice, "小明");
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MemoryService>();
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.BobId, "10002"), "项目", default))).IsEmpty();
        var wrong = await f.Alice.PostAsJsonAsync($"/api/v1/me/memories/{f.SourceId}/sharing", new { scope = "SpaceShared", contextSpaceId = f.OtherSpaceId });
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var shared = await ShareAsync(f.Alice, f.SourceId, "SpaceShared", f.SpaceId);
        await f.Bob.PutAsJsonAsync("/api/v1/me/memory-settings", new { allowMemory = false });
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.BobId, "10002"), "项目", default)).Any(x => x.Id == shared)).IsTrue();
        foreach (var group in new string?[] { "10003", "10004", null })
            await Assert.That((await service.RetrieveAsync(new(f.BotId, f.BobId, group), "项目", default)).Any(x => x.Id == shared)).IsFalse();
        var secondBot = await f.Owner.PostAsJsonAsync("/api/v1/bots", new { name = "Other", identity = "Other" });
        var otherBotId = (await secondBot.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var crossBot = await f.Owner.PutAsJsonAsync($"/api/v1/bots/{otherBotId}/groups/10002/policy", new { contextSpaceId = f.SpaceId });
        await Assert.That(crossBot.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await f.Alice.GetAsync($"/api/v1/bots/{f.BotId}/context-spaces")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await f.Alice.PostAsJsonAsync($"/api/v1/bots/{f.BotId}/context-spaces", new { name = "Unauthorized" })).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await f.Alice.PutAsJsonAsync($"/api/v1/bots/{f.BotId}/context-spaces/{f.SpaceId}", new { name = "Unauthorized" })).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await f.Alice.DeleteAsync($"/api/v1/bots/{f.BotId}/context-spaces/{f.SpaceId}")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await f.Owner.PostAsJsonAsync($"/api/v1/bots/{f.BotId}/context-spaces", new { name = " " })).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await f.Owner.GetAsync($"/api/v1/bots/{f.BotId}/groups")).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await f.Owner.GetAsync($"/api/v1/bots/{f.BotId}/deliveries")).StatusCode).IsEqualTo(HttpStatusCode.OK);
        var move = await f.Owner.PutAsJsonAsync($"/api/v1/bots/{f.BotId}/groups/10001/policy", new { contextSpaceId = f.OtherSpaceId });
        await Assert.That(move.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.BobId, "10002"), "项目", default)).Any(x => x.Id == shared)).IsFalse();
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.BobId, "10003"), "项目", default))).IsEmpty();
        var ownMemories = await f.Alice.GetFromJsonAsync<JsonElement>("/api/v1/me/memories");
        var unavailable = ownMemories.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == f.SourceId)
            .GetProperty("sharing").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == shared);
        await Assert.That(unavailable.GetProperty("available").GetBoolean()).IsFalse();
        var reshared = await ShareAsync(f.Alice, f.SourceId, "SpaceShared", f.OtherSpaceId);
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.BobId, "10003"), "项目", default)).Any(x => x.Id == reshared)).IsTrue();
        await Assert.That((await f.Owner.DeleteAsync($"/api/v1/bots/{f.BotId}/context-spaces/{f.OtherSpaceId}")).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await service.RetrieveAsync(new(f.BotId, f.BobId, "10003"), "项目", default))).IsEmpty();
    }

    [Test]
    public async Task WithdrawalOnlyInvalidatesDependentHistoryAndCorrectionRevokesEveryCopy()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        await EnableAsync(f.Alice, "小明");
        var shared = await ShareAsync(f.Alice, f.SourceId, "SpaceShared", f.SpaceId);
        var personal = await ShareAsync(f.Alice, f.SourceId, "UserShared");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var conversation = new Conversation { BotId = f.BotId, Kind = ConversationKind.Group, ExternalId = "dependent" };
        var unrelated = new Conversation { BotId = f.BotId, Kind = ConversationKind.Group, ExternalId = "unrelated" };
        var summaryOnly = new Conversation { BotId = f.BotId, Kind = ConversationKind.Group, ExternalId = "summary-only" };
        db.Conversations.AddRange(conversation, unrelated, summaryOnly);
        var session = new ConversationSession { BotId = f.BotId, ConversationId = conversation.Id };
        var otherSession = new ConversationSession { BotId = f.BotId, ConversationId = unrelated.Id };
        var summarySession = new ConversationSession { BotId = f.BotId, ConversationId = summaryOnly.Id };
        db.Sessions.AddRange(session, otherSession, summarySession);
        var sourceJob = Jobs.Add(db, f.BotId, JobTypes.Chat, new IdPayload(Guid.NewGuid()), conversation.Id);
        var otherJob = Jobs.Add(db, f.BotId, JobTypes.Chat, new IdPayload(Guid.NewGuid()), unrelated.Id);
        string Snapshot(Guid sessionId, Guid[] memories) => Json.Write(new ContextSnapshot(f.BotId, sessionId, 1, 1, null, 0, 0, memories, "test"));
        var message = new ChatMessage { BotId = f.BotId, ConversationId = conversation.Id, SessionId = session.Id,
            Role = "assistant", ExternalMessageId = "generated:" + sourceJob.Id, Text = "shared derived answer", ContextSnapshotJson = Snapshot(session.Id, [shared]) };
        var otherMessage = new ChatMessage { BotId = f.BotId, ConversationId = unrelated.Id, SessionId = otherSession.Id,
            Role = "assistant", ExternalMessageId = "generated:" + otherJob.Id, Text = "unrelated history", ContextSnapshotJson = Snapshot(otherSession.Id, []) };
        db.Messages.AddRange(message, otherMessage);
        db.Summaries.AddRange(new ConversationSummary { BotId = f.BotId, SessionId = session.Id, Text = "dependent summary" },
            new ConversationSummary { BotId = f.BotId, SessionId = otherSession.Id, Text = "unrelated summary" },
            new ConversationSummary { BotId = f.BotId, SessionId = summarySession.Id, Text = "summary with expired source message", MemoryIdsJson = Json.Write(new[] { shared }) });
        var batch = new OutgoingMessageBatch { BotId = f.BotId, ConversationId = conversation.Id, SessionId = session.Id, SourceJobId = sourceJob.Id };
        var otherBatch = new OutgoingMessageBatch { BotId = f.BotId, ConversationId = unrelated.Id, SessionId = otherSession.Id, SourceJobId = otherJob.Id };
        db.Batches.AddRange(batch, otherBatch);
        var part = new OutgoingMessagePart { BatchId = batch.Id, Text = "shared pending answer" };
        var otherPart = new OutgoingMessagePart { BatchId = otherBatch.Id, Text = "unrelated pending answer" };
        db.Parts.AddRange(part, otherPart);
        await db.SaveChangesAsync();
        var withdrawn = await f.Alice.DeleteAsync($"/api/v1/me/memories/{f.SourceId}/sharing/{shared}");
        await Assert.That(withdrawn.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        db.ChangeTracker.Clear();
        var affected = await db.Messages.SingleAsync(x => x.Id == message.Id);
        await Assert.That(affected.ContextInvalidated).IsTrue();
        await Assert.That(affected.Text).IsEqualTo("shared derived answer");
        await Assert.That((await db.Messages.SingleAsync(x => x.Id == otherMessage.Id)).ContextInvalidated).IsFalse();
        await Assert.That((await db.Summaries.SingleAsync(x => x.SessionId == session.Id)).ContextInvalidated).IsTrue();
        await Assert.That((await db.Summaries.SingleAsync(x => x.SessionId == otherSession.Id)).ContextInvalidated).IsFalse();
        await Assert.That((await db.Summaries.SingleAsync(x => x.SessionId == summarySession.Id)).ContextInvalidated).IsTrue();
        await Assert.That((await db.Parts.SingleAsync(x => x.Id == part.Id)).Status).IsEqualTo(DeliveryStatus.Failed);
        await Assert.That((await db.Parts.SingleAsync(x => x.Id == otherPart.Id)).Status).IsEqualTo(DeliveryStatus.Pending);
        await Assert.That((await db.Memories.SingleAsync(x => x.Id == f.SourceId)).Text).Contains("项目");
        await Assert.That((await f.Alice.PatchAsJsonAsync($"/api/v1/me/memories/{f.SourceId}", new { text = "项目进入下一阶段。" })).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        db.ChangeTracker.Clear();
        await Assert.That((await db.Memories.SingleAsync(x => x.Id == personal)).Status).IsEqualTo(MemoryStatus.Revoked);
        await Assert.That(await db.Memories.Where(x => x.SharedFromMemoryId == f.SourceId && x.Status != MemoryStatus.Revoked).ToListAsync()).IsEmpty();
        await Assert.That(await db.Memories.Where(x => x.BotId == f.BotId && x.Text == "项目进入下一阶段。" && x.SharedFromMemoryId == null).ToListAsync()).HasSingleItem();
    }

    [Test]
    public async Task EachSenderReceivesTheirOwnNameAndSharedFactsRetainTheOriginalAuthor()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        await EnableAsync(f.Alice, "小明");
        await f.Bob.PutAsJsonAsync("/api/v1/me/memory-settings", new { allowMemory = false, preferredName = "小李" });
        await ShareAsync(f.Alice, f.SourceId, "SpaceShared", f.SpaceId);
        await ShareAsync(f.Alice, f.PersonalId, "UserShared");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();
        var workflow = scope.ServiceProvider.GetRequiredService<ChatWorkflow>();
        async Task Send(string id, string sender, string group, string text)
        {
            await ingestor.IngestAsync(f.BindingId, new NormalizedEvent("999", id, sender, group, ConversationKind.Group,
                [new MessageSegment(SegmentKind.Mention, "999"), new MessageSegment(SegmentKind.Text, text)], DateTimeOffset.UtcNow), default);
            var message = await db.Messages.AsNoTracking().SingleAsync(x => x.BotId == f.BotId && x.ExternalMessageId == id);
            var job = await db.Outbox.AsNoTracking().SingleAsync(x => x.BotId == f.BotId && x.Type == JobTypes.Chat && x.Payload.Contains(message.Id.ToString()));
            await workflow.RunAsync(job.Id, default);
        }
        await Send("bob-1", "22222", "10002", "继续讨论部署");
        using var request = JsonDocument.Parse(app.ModelRequests[0]);
        var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var current = messages[^1].GetProperty("content").GetString()!;
        await Assert.That(current).Contains(Json.Write("小李"));
        await Assert.That(current).DoesNotContain(Json.Write("小明"));
        await Assert.That(current).DoesNotContain("树莓");
        var reference = messages[1].GetProperty("content").GetString()!;
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(x => x.BotId == f.BotId && x.ExternalId == "10002");
        await Assert.That(reference).Contains(SpeakerTags.For(conversation.Id, f.AliceId));
        await Assert.That(reference).Contains("scope=SpaceShared");
        await Assert.That(reference).DoesNotContain("偏好简洁");
        await Send("alice-1", "11111", "10003", "请简短些");
        using var otherRequest = JsonDocument.Parse(app.ModelRequests[1]);
        var other = otherRequest.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        await Assert.That(other[^1].GetProperty("content").GetString()!).Contains(Json.Write("小明"));
        await Assert.That(other[1].GetProperty("content").GetString()!).Contains("scope=UserShared");
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
    public async Task CorrectionSurvivesInvalidCachedProvenance(string snapshot, string summaryIds,
        bool invalidSnapshot, bool invalidSummary)
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var conversation = new Conversation { BotId = f.BotId, Kind = ConversationKind.Group, ExternalId = "legacy-cache" };
        var session = new ConversationSession { BotId = f.BotId, ConversationId = conversation.Id };
        db.Conversations.Add(conversation);
        db.Sessions.Add(session);
        var job = Jobs.Add(db, f.BotId, JobTypes.Chat, new IdPayload(Guid.NewGuid()), conversation.Id);
        var message = new ChatMessage { BotId = f.BotId, ConversationId = conversation.Id, SessionId = session.Id,
            Role = "assistant", ExternalMessageId = "generated:" + job.Id, Text = "legacy cached answer", ContextSnapshotJson = snapshot };
        var summary = new ConversationSummary { BotId = f.BotId, SessionId = session.Id,
            Text = "legacy summary", MemoryIdsJson = summaryIds };
        var batch = new OutgoingMessageBatch { BotId = f.BotId, ConversationId = conversation.Id, SessionId = session.Id, SourceJobId = job.Id };
        var part = new OutgoingMessagePart { BatchId = batch.Id, Text = message.Text };
        db.Messages.Add(message);
        db.Summaries.Add(summary);
        db.Batches.Add(batch);
        db.Parts.Add(part);
        await db.SaveChangesAsync();

        var response = await f.Alice.PatchAsJsonAsync($"/api/v1/me/memories/{f.SourceId}", new { text = "修正后的项目状态" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        db.ChangeTracker.Clear();
        var oldMemory = await db.Memories.SingleAsync(x => x.Id == f.SourceId);
        await Assert.That(oldMemory.Status).IsEqualTo(MemoryStatus.Revoked);
        await Assert.That(await db.Memories.Where(x => x.SubjectId == f.AliceId && x.Text == "修正后的项目状态" &&
            x.Status != MemoryStatus.Revoked).ToListAsync()).HasSingleItem();
        var cachedMessage = await db.Messages.SingleAsync(x => x.Id == message.Id);
        await Assert.That(cachedMessage.ContextInvalidated).IsEqualTo(invalidSnapshot);
        await Assert.That(cachedMessage.Text).IsEqualTo("legacy cached answer");
        var cachedSummary = await db.Summaries.SingleAsync(x => x.Id == summary.Id);
        await Assert.That(cachedSummary.ContextInvalidated).IsEqualTo(invalidSnapshot || invalidSummary);
        await Assert.That(cachedSummary.Text).IsEqualTo("legacy summary");
        await Assert.That((await db.Parts.SingleAsync(x => x.Id == part.Id)).Status)
            .IsEqualTo(invalidSnapshot ? DeliveryStatus.Failed : DeliveryStatus.Pending);
    }

    [Test]
    public async Task MaintenanceSweepPreservesPendingWorkAndRemovesExpiredData()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var conversation = new Conversation { BotId = f.BotId, ExternalId = "maintenance", Kind = ConversationKind.Group };
        var pendingSession = new ConversationSession { BotId = f.BotId, ConversationId = conversation.Id, Status = SessionStatus.Reflecting };
        var doneSession = new ConversationSession { BotId = f.BotId, ConversationId = conversation.Id, Status = SessionStatus.Reflecting };
        db.Conversations.Add(conversation);
        db.Sessions.AddRange(pendingSession, doneSession);
        Jobs.Add(db, f.BotId, JobTypes.Reflection,
            new ReflectionJob(pendingSession.Id, f.AliceId, 0, 0, "full"), conversation.Id);
        var expired = new ChatMessage { BotId = f.BotId, ConversationId = conversation.Id, SessionId = doneSession.Id,
            ExternalMessageId = "expired", ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var retained = new ChatMessage { BotId = f.BotId, ConversationId = conversation.Id, SessionId = pendingSession.Id,
            ExternalMessageId = "retained", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.Messages.AddRange(expired, retained);
        var expiredMedia = new MediaAsset { BotId = f.BotId, MessageId = expired.Id, ExpiresAt = expired.ExpiresAt };
        var retainedMedia = new MediaAsset { BotId = f.BotId, MessageId = retained.Id, ExpiresAt = retained.ExpiresAt };
        db.Media.AddRange(expiredMedia, retainedMedia);
        OutgoingMessageBatch Batch(DeliveryStatus status, int age)
        {
            var job = Jobs.Add(db, f.BotId, JobTypes.Chat, new IdPayload(Guid.NewGuid()), conversation.Id);
            job.Status = JobStatus.Completed;
            job.CreatedAt = DateTimeOffset.UtcNow.AddDays(-age);
            var batch = new OutgoingMessageBatch { BotId = f.BotId, ConversationId = conversation.Id, SessionId = doneSession.Id, SourceJobId = job.Id };
            db.Batches.Add(batch);
            db.Parts.Add(new OutgoingMessagePart { BatchId = batch.Id, Text = "delivery", Status = status });
            return batch;
        }
        var expiredBatch = Batch(DeliveryStatus.Sent, 31);
        var pendingBatch = Batch(DeliveryStatus.Pending, 31);
        var recentBatch = Batch(DeliveryStatus.Sent, 1);
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().SweepAsync(default);
        db.ChangeTracker.Clear();
        await Assert.That(await db.Messages.AnyAsync(x => x.Id == expired.Id)).IsFalse();
        await Assert.That(await db.Messages.AnyAsync(x => x.Id == retained.Id)).IsTrue();
        await Assert.That(await db.Media.AnyAsync(x => x.Id == expiredMedia.Id)).IsFalse();
        await Assert.That(await db.Media.AnyAsync(x => x.Id == retainedMedia.Id)).IsTrue();
        await Assert.That(await db.Batches.AnyAsync(x => x.Id == expiredBatch.Id)).IsFalse();
        await Assert.That(await db.Parts.AnyAsync(x => x.BatchId == expiredBatch.Id)).IsFalse();
        await Assert.That(await db.Batches.AnyAsync(x => x.Id == pendingBatch.Id)).IsTrue();
        await Assert.That(await db.Batches.AnyAsync(x => x.Id == recentBatch.Id)).IsTrue();
        await Assert.That((await db.Sessions.SingleAsync(x => x.Id == pendingSession.Id)).Status).IsEqualTo(SessionStatus.Reflecting);
        await Assert.That((await db.Sessions.SingleAsync(x => x.Id == doneSession.Id)).Status).IsEqualTo(SessionStatus.Closed);
    }

    private static async Task EnableAsync(HttpClient client, string name)
    {
        var response = await client.PutAsJsonAsync("/api/v1/me/memory-settings", new
            { allowMemory = true, allowCrossGroupMemory = true, preferredName = name });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private static async Task<Guid> ShareAsync(HttpClient client, Guid source, string scope, Guid? space = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/me/memories/{source}/sharing", new { scope, contextSpaceId = space });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
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
        async Task<Guid> Space(string name)
        {
            var response = await owner.Client.PostAsJsonAsync($"/api/v1/bots/{bot}/context-spaces", new { name });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var shared = await Space("项目组");
        var other = await Space("其他项目");
        foreach (var group in new[] { "10001", "10002", "10003", "10004" })
        {
            var response = await owner.Client.PutAsJsonAsync($"/api/v1/bots/{bot}/groups/{group}/policy", new
                { contextSpaceId = group == "10003" ? other : group == "10004" ? (Guid?)null : shared });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var a = new LogicalUser { AllowMemory = true };
        var b = new LogicalUser { AllowMemory = true };
        db.LogicalUsers.AddRange(a, b);
        (await db.Users.SingleAsync(x => x.Id == alice.Id)).LogicalUserId = a.Id;
        (await db.Users.SingleAsync(x => x.Id == bob.Id)).LogicalUserId = b.Id;
        db.PlatformIdentities.AddRange(new PlatformIdentity { UserId = a.Id, ExternalId = "11111" }, new PlatformIdentity { UserId = b.Id, ExternalId = "22222" });
        var binding = new PlatformBinding { BotId = bot, PlatformAccountId = "999" };
        db.Bindings.Add(binding);
        db.Models.Add(new ModelProfile { BotId = bot, TaskKind = TaskKind.Chat, Endpoint = "https://model.example/v1/", Model = "test",
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key") });
        var source = new Core.Memory { BotId = bot, SubjectId = a.Id, Scope = MemoryScope.GroupUser, ScopeOwnerId = "10001",
            FactKey = "project-progress", Text = "项目已完成测试，下一步部署。", Status = MemoryStatus.Active };
        var personal = new Core.Memory { BotId = bot, SubjectId = a.Id, Scope = MemoryScope.Private, ScopeOwnerId = a.Id.ToString(),
            FactKey = "answer-style", Text = "偏好简洁回答。", Status = MemoryStatus.Active };
        db.Memories.AddRange(source, personal);
        await db.SaveChangesAsync();
        return new(owner.Client, alice.Client, bob.Client, bot, a.Id, b.Id, source.Id, personal.Id, shared, other, binding.Id);
    }

    private sealed record Fixture(HttpClient Owner, HttpClient Alice, HttpClient Bob, Guid BotId, Guid AliceId,
        Guid BobId, Guid SourceId, Guid PersonalId, Guid SpaceId, Guid OtherSpaceId, Guid BindingId);
}
