using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Wonder.AI;
using Wonder.Core;
using Wonder.Infrastructure;
using ChatMessage = Wonder.Core.ChatMessage;

namespace Wonder.IntegrationTests;

public sealed class ConversationContinuityTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-19T15:58:00Z");

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DsmlBodyIsRejectedWithoutExecutingToolsOrPersistingAReply(bool tools)
    {
        using var app = new TestApp(new Clock(Start));
        var f = await SeedAsync(app, tools: tools);
        var next = await QueueAsync(app, f.Binding, "dsml", "Miku记得下午发生了什么事情吗");
        app.ModelResponses.Enqueue(Response("""
            <｜｜DSML｜｜ calls>
            <｜｜DSML｜｜ invoke name="search_conversation_history">
            <｜｜DSML｜｜ parameter name="query" string="true">Wonder</｜｜DSML｜｜ parameter>
            </｜｜DSML｜｜ invoke>
            </｜｜DSML｜｜ calls>
            """));
        await Assert.That(() => RunAsync(app, next.Job)).ThrowsExactly<ModelResponseException>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.Parts.AnyAsync()).IsFalse();
        await Assert.That(await db.Messages.AnyAsync(x => x.Role == "assistant")).IsFalse();
        await Assert.That(await db.Usage.CountAsync()).IsEqualTo(1);
        await Assert.That(app.Sender.Sent).IsEmpty();
    }

    [Test]
    public async Task LegacyQueuedDsmlIsBlockedAcrossPartBoundariesAndInvalidated()
    {
        using var app = new TestApp(new Clock(Start));
        var f = await SeedAsync(app);
        var next = await QueueAsync(app, f.Binding, "legacy-dsml", "查一下下午的事情");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var original = await db.Outbox.SingleAsync(x => x.Id == next.Job);
        ChatWorkflow.QueueReply(db, original, next.Message.SessionId!.Value, "placeholder");
        var batch = db.Batches.Local.Single();
        db.Parts.Local.Single().Text = "<｜｜DS";
        db.Parts.Add(new OutgoingMessagePart { BatchId = batch.Id, Sequence = 1, Text = "ML｜｜ calls>unfinished" });
        var generated = new ChatMessage { BotId = f.Bot, ConversationId = next.Message.ConversationId,
            SessionId = next.Message.SessionId, Role = "assistant", ExternalMessageId = "generated:" + next.Job,
            Text = "<｜｜DSML｜｜ calls>unfinished" };
        db.Messages.Add(generated);
        await db.SaveChangesAsync();
        var outgoing = await db.Outbox.SingleAsync(x => x.Type == JobTypes.Outgoing);
        await scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(outgoing.Id, default);
        await scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(outgoing.Id, default);
        db.ChangeTracker.Clear();
        await Assert.That(app.Sender.Sent).IsEmpty();
        await Assert.That(await db.Parts.AllAsync(x => x.Status == DeliveryStatus.Failed)).IsTrue();
        await Assert.That((await db.Messages.SingleAsync(x => x.Id == generated.Id)).ContextInvalidated).IsTrue();
    }

    [Test]
    public async Task StandardFunctionLoopOnlySendsTheFinalAnswer()
    {
        using var app = new TestApp(new Clock(Start));
        var f = await SeedAsync(app, tools: true);
        var next = await QueueAsync(app, f.Binding, "standard-loop", "现在几点");
        app.ModelResponses.Enqueue(Json.Write(new { choices = new[] { new { finish_reason = "tool_calls", message = new
        {
            content = "我先查询时间。", reasoning_content = "private chain of thought",
            tool_calls = new[] { new { id = "clock", type = "function", function = new { name = "get_current_time", arguments = "{}" } } }
        } } }, usage = new { prompt_tokens = 2, completion_tokens = 3 } }));
        app.ModelResponses.Enqueue(Response("现在是晚上十一点五十八分。"));
        await RunAsync(app, next.Job);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That((await db.Parts.SingleAsync()).Text).IsEqualTo("现在是晚上十一点五十八分。");
        await Assert.That(await db.Usage.CountAsync()).IsEqualTo(2);
        await Assert.That((await db.Messages.SingleAsync(x => x.Role == "assistant")).Text).DoesNotContain("我先查询");
    }

    [Test]
    public async Task TimeoutStartsANewSessionButKeepsTheConversationAndOriginalDates()
    {
        var clock = new Clock(Start);
        using var app = new TestApp(clock);
        var f = await SeedAsync(app);
        var first = await QueueAsync(app, f.Binding, "first", "我们明天继续讨论小屋的配色。");
        await RunAsync(app, first.Job);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            var outgoing = await db.Outbox.SingleAsync(x => x.BotId == f.Bot && x.Type == JobTypes.Outgoing);
            await scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(outgoing.Id, default);
            await db.Sessions.Where(x => x.Id == first.Message.SessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeadlineAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().SweepAsync(default);
            await Assert.That((await db.Sessions.AsNoTracking().SingleAsync(x => x.Id == first.Message.SessionId)).Status).IsEqualTo(SessionStatus.Closed);
        }
        clock.Now = DateTimeOffset.Parse("2026-10-20T01:00:00Z");
        var next = await QueueAsync(app, f.Binding, "next", "我们接着说吧。");
        await Assert.That(next.Message.SessionId).IsNotEqualTo(first.Message.SessionId);
        await RunAsync(app, next.Job);
        using var request = JsonDocument.Parse(app.ModelRequests[^1]);
        var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var history = string.Join("\n", messages.Select(x => x.GetProperty("content").GetString()));
        await Assert.That(history).Contains("我们明天继续讨论小屋的配色。");
        await Assert.That(history).Contains("2026-10-19T23:58:00");
        using var time = JsonDocument.Parse(messages[0].GetProperty("content").GetString()!.Split("[Application time context]\n")[1]);
        await Assert.That(time.RootElement.GetProperty("currentTime").GetString()).IsEqualTo("2026-10-20T09:00:00.0000000+08:00");
        await Assert.That(time.RootElement.GetProperty("continuingPreviousSession").GetBoolean()).IsTrue();
        await Assert.That(time.RootElement.GetProperty("elapsedSeconds").GetInt64()).IsGreaterThan(0);
    }

    [Test]
    public async Task RestartWithTheSameDatabaseRecoversTheActiveConversation()
    {
        var directory = Path.GetFullPath(".local");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "continuity-" + Guid.NewGuid().ToString("N") + ".db");
        var clock = new Clock(Start);
        try
        {
            (Guid Bot, Guid Binding) f;
            Guid? sessionId;
            using (var firstApp = new TestApp(clock, file))
            {
                f = await SeedAsync(firstApp);
                var first = await QueueAsync(firstApp, f.Binding, "before-restart", "窗帘我们刚才选了蓝色。");
                sessionId = first.Message.SessionId;
                await RunAsync(firstApp, first.Job);
            }
            clock.Now = Start.AddMinutes(1);
            using var restarted = new TestApp(clock, file);
            var next = await QueueAsync(restarted, f.Binding, "after-restart", "继续刚才的话题。");
            await Assert.That(next.Message.SessionId).IsEqualTo(sessionId);
            await RunAsync(restarted, next.Job);
            using var sent = JsonDocument.Parse(restarted.ModelRequests.Single());
            await Assert.That(string.Join("\n", sent.RootElement.GetProperty("messages").EnumerateArray()
                .Select(x => x.GetProperty("content").GetString()))).Contains("窗帘我们刚才选了蓝色。");
        }
        finally { File.Delete(file); }
    }

    [Test]
    public async Task ToolsFindOldOriginalRecordsOutsideTheWindowAndKeepOtherConversationsPrivate()
    {
        var clock = new Clock(Start);
        using var app = new TestApp(clock);
        var f = await SeedAsync(app, tools: true, contextWindow: 8192);
        var old = await QueueAsync(app, f.Binding, "old", "小屋暗号是蒲公英742", "group-one");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            db.Messages.AddRange(Enumerable.Range(0, 45).Select(i => new ChatMessage
            {
                BotId = f.Bot, ConversationId = old.Message.ConversationId, SessionId = old.Message.SessionId,
                SenderId = old.Message.SenderId, ExternalMessageId = "padding-" + i, Text = "普通话题 " + i,
                ExpiresAt = Start.AddDays(90), CreatedAt = Start.AddMinutes(i + 1)
            }));
            db.Messages.AddRange(new ChatMessage { BotId = f.Bot, ConversationId = old.Message.ConversationId,
                SessionId = old.Message.SessionId, ExternalMessageId = "revoked", Text = "暗号INVALIDATED", ContextInvalidated = true, ExpiresAt = Start.AddDays(90) },
                new ChatMessage { BotId = f.Bot, ConversationId = old.Message.ConversationId, SessionId = old.Message.SessionId,
                    ExternalMessageId = "expired", Text = "暗号EXPIRED", ExpiresAt = Start.AddDays(-1) });
            await db.SaveChangesAsync();
            await db.Sessions.Where(x => x.Id == old.Message.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.Closed));
        }
        await QueueAsync(app, f.Binding, "other-group", "暗号OTHER_GROUP", "group-two");
        await QueueAsync(app, f.Binding, "private", "暗号PRIVATE");
        var next = await QueueAsync(app, f.Binding, "ask", "之前小屋暗号是什么？", "group-one");
        await QueueAsync(app, f.Binding, "future", "暗号FUTURE", "group-one");
        app.ModelResponses.Enqueue(Json.Write(new { choices = new[] { new { finish_reason = "tool_calls", message = new
        {
            content = (string?)null, reasoning_content = "check only the current conversation",
            tool_calls = new[]
            {
                new { id = "history-call", type = "function", function = new { name = "search_conversation_history", arguments = "{\"query\":\"暗号\"}" } },
                new { id = "time-call", type = "function", function = new { name = "get_current_time", arguments = "{\"time_zone\":\"UTC\"}" } }
            }
        } } }, usage = new { prompt_tokens = 20, completion_tokens = 10 } }));
        app.ModelResponses.Enqueue(Response("查到了，你之前说的是蒲公英742。"));
        await RunAsync(app, next.Job);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(2);
        using var initial = JsonDocument.Parse(app.ModelRequests[0]);
        await Assert.That(initial.RootElement.GetProperty("messages").EnumerateArray()
            .Any(x => x.GetProperty("content").GetString()!.Contains("小屋暗号是蒲公英742"))).IsFalse();
        using var final = JsonDocument.Parse(app.ModelRequests[1]);
        var messages = final.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var tool = messages.Single(x => x.GetProperty("role").GetString() == "tool" && x.GetProperty("tool_call_id").GetString() == "history-call");
        using var result = JsonDocument.Parse(tool.GetProperty("content").GetString()!);
        var records = result.RootElement.GetProperty("records").EnumerateArray().ToArray();
        await Assert.That(records).HasSingleItem();
        await Assert.That(records[0].GetProperty("text").GetString()).IsEqualTo("小屋暗号是蒲公英742");
        await Assert.That(records[0].GetProperty("author").GetString()).IsEqualTo(SpeakerTags.For(old.Message.ConversationId, old.Message.SenderId));
        await Assert.That(records[0].GetProperty("sentAt").GetString()).Contains("2026-10-19T23:58");
        using var time = JsonDocument.Parse(messages.Single(x => x.GetProperty("role").GetString() == "tool" &&
            x.GetProperty("tool_call_id").GetString() == "time-call").GetProperty("content").GetString()!);
        await Assert.That(time.RootElement.GetProperty("timeZone").GetString()).IsEqualTo("UTC");
        await Assert.That(messages.Single(x => x.GetProperty("role").GetString() == "assistant")
            .GetProperty("reasoning_content").GetString()).IsEqualTo("check only the current conversation");
        using var check = app.Services.CreateScope();
        var dbCheck = check.ServiceProvider.GetRequiredService<WonderDbContext>();
        var reply = await dbCheck.Messages.SingleAsync(x => x.InReplyToSequence == next.Message.Sequence);
        var snapshot = Json.Read<ContextSnapshot>(reply.ContextSnapshotJson!);
        await Assert.That(snapshot.SourceMessageIds!).Contains(old.Message.Id);
        await Assert.That((await dbCheck.Parts.SingleAsync()).Text).IsEqualTo("查到了，你之前说的是蒲公英742。");
        await Assert.That(await dbCheck.Usage.CountAsync()).IsEqualTo(2);
    }

    [Test]
    public async Task NewSessionUsesThePreviousSummaryAndRollingSummariesCarryItsSources()
    {
        using var app = new TestApp(new Clock(Start));
        var f = await SeedAsync(app);
        var first = await QueueAsync(app, f.Binding, "summarized", "旧会话已概括的原文");
        var memoryId = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            db.Summaries.Add(new ConversationSummary { BotId = f.Bot, SessionId = first.Message.SessionId!.Value,
                ThroughSequence = first.Message.Sequence, Text = "小屋配色仍待确定。", MemoryIdsJson = Json.Write(new[] { memoryId }),
                MessageIdsJson = Json.Write(new[] { first.Message.Id }), SubjectIdsJson = Json.Write(new[] { first.Message.SenderId!.Value }) });
            await db.SaveChangesAsync();
            await db.Sessions.Where(x => x.Id == first.Message.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.Closed));
        }
        var next = await QueueAsync(app, f.Binding, "new", "继续配色");
        await RunAsync(app, next.Job);
        using var chat = JsonDocument.Parse(app.ModelRequests[0]);
        var text = string.Join("\n", chat.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("content").GetString()));
        await Assert.That(text).Contains("小屋配色仍待确定。");
        await Assert.That(text).DoesNotContain("旧会话已概括的原文");
        using var finalScope = app.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var rows = Enumerable.Range(0, 26).Select(i => new ChatMessage { BotId = f.Bot, ConversationId = next.Message.ConversationId,
            SessionId = next.Message.SessionId, SenderId = first.Message.SenderId, ExternalMessageId = "new-summary-" + i,
            Text = "配色细节 " + i, CreatedAt = Start, ExpiresAt = Start.AddDays(90) }).ToArray();
        finalDb.Messages.AddRange(rows);
        await finalDb.SaveChangesAsync();
        var job = Jobs.Add(finalDb, f.Bot, JobTypes.Summary, new SummaryJob(next.Message.SessionId!.Value, rows.Max(x => x.Sequence)), next.Message.ConversationId);
        await finalDb.SaveChangesAsync();
        await finalScope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().RunAsync(job.Id, default);
        using var summaryRequest = JsonDocument.Parse(app.ModelRequests[1]);
        using var summaryInput = JsonDocument.Parse(summaryRequest.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        await Assert.That(summaryInput.RootElement.GetProperty("previous").GetString()).IsEqualTo("小屋配色仍待确定。");
        var saved = await finalDb.Summaries.AsNoTracking().SingleAsync(x => x.SessionId == next.Message.SessionId);
        await Assert.That(Json.Read<Guid[]>(saved.MemoryIdsJson)).Contains(memoryId);
        await Assert.That(Json.Read<Guid[]>(saved.MessageIdsJson)).Contains(first.Message.Id);
    }

    [Test]
    public async Task RevocationInvalidatesLaterSessionRepliesEvenWhenTheAuthorDidNotPostThere()
    {
        using var app = new TestApp(new Clock(Start));
        var f = await SeedAsync(app);
        var first = await QueueAsync(app, f.Binding, "alice", "项目代号为纸飞机", "group-one");
        Guid memoryId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            var memory = new Core.Memory { BotId = f.Bot, SubjectId = first.Message.SenderId!.Value, Scope = MemoryScope.GroupUser,
                ScopeOwnerId = "group-one", Text = "项目代号为纸飞机", FactKey = "project-code" };
            memoryId = memory.Id;
            db.Memories.Add(memory);
            await db.SaveChangesAsync();
            await db.Sessions.Where(x => x.Id == first.Message.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.Closed));
        }
        var next = await QueueAsync(app, f.Binding, "bob", "刚才的项目呢", "group-one", sender: "222");
        await RunAsync(app, next.Job);
        using var scope2 = app.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<WonderDbContext>();
        var reply = await db2.Messages.AsNoTracking().SingleAsync(x => x.InReplyToSequence == next.Message.Sequence);
        var snapshot = Json.Read<ContextSnapshot>(reply.ContextSnapshotJson!);
        await Assert.That(snapshot.SourceUserIds!).Contains(first.Message.SenderId!.Value);
        db2.Summaries.Add(new ConversationSummary { BotId = f.Bot, SessionId = next.Message.SessionId!.Value,
            Text = "derived from the old session", MessageIdsJson = Json.Write(snapshot.SourceMessageIds) });
        await db2.SaveChangesAsync();
        await scope2.ServiceProvider.GetRequiredService<MemoryService>().RevokeAsync(first.Message.SenderId.Value, memoryId, null, default);
        await Assert.That((await db2.Messages.AsNoTracking().SingleAsync(x => x.Id == reply.Id)).ContextInvalidated).IsTrue();
        await Assert.That((await db2.Summaries.AsNoTracking().SingleAsync(x => x.SessionId == next.Message.SessionId)).ContextInvalidated).IsTrue();
        await Assert.That((await db2.Parts.SingleAsync()).Status).IsEqualTo(DeliveryStatus.Failed);
        var (summary, history) = await scope2.ServiceProvider.GetRequiredService<ConversationHistory>()
            .LoadAsync(f.Bot, next.Message.ConversationId, long.MaxValue, default);
        await Assert.That(summary).IsNull();
        await Assert.That(history.Any(x => x.Id == first.Message.Id || x.Id == reply.Id)).IsFalse();
    }

    [Test]
    public async Task RepeatedToolCallsStopAndRecordEveryAttemptWithoutSendingReasoning()
    {
        using var app = new TestApp(new Clock(Start));
        var f = await SeedAsync(app, tools: true);
        var next = await QueueAsync(app, f.Binding, "loop", "现在几点");
        for (var i = 0; i < 4; i++) app.ModelResponses.Enqueue(Json.Write(new { choices = new[] { new
        {
            finish_reason = "tool_calls", message = new { content = (string?)null, reasoning_content = "internal-only",
                tool_calls = new[] { new { id = "time-" + i, type = "function", function = new { name = "get_current_time", arguments = "{}" } } } }
        } }, usage = new { prompt_tokens = 1, completion_tokens = 1 } }));
        using var scope = app.Services.CreateScope();
        await Assert.That(() => scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(next.Job, default)).ThrowsExactly<ModelResponseException>();
        await Assert.That(app.ModelRequests.Count).IsEqualTo(4);
        using var last = JsonDocument.Parse(app.ModelRequests[^1]);
        await Assert.That(last.RootElement.GetProperty("tool_choice").GetString()).IsEqualTo("none");
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.Parts.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.Usage.CountAsync()).IsEqualTo(4);
    }

    [Test]
    public async Task SavedNonTriggerGroupMessagesAreAvailableToTheNextReply()
    {
        using var app = new TestApp(new Clock(Start));
        var f = await SeedAsync(app);
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MessageIngestor>().IngestAsync(f.Binding,
                new NormalizedEvent("999", "ambient", "333", "group-one", ConversationKind.Group,
                    [new MessageSegment(SegmentKind.Text, "窗帘候选是米白色")], Start), default);
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            await Assert.That((await db.Messages.SingleAsync(x => x.ExternalMessageId == "ambient")).SessionId).IsNull();
            await Assert.That(await db.Outbox.CountAsync(x => x.Type == JobTypes.Chat)).IsEqualTo(0);
        }
        var next = await QueueAsync(app, f.Binding, "ambient-ask", "刚才说的窗帘呢", "group-one");
        await RunAsync(app, next.Job);
        using var sent = JsonDocument.Parse(app.ModelRequests.Single());
        await Assert.That(string.Join("\n", sent.RootElement.GetProperty("messages").EnumerateArray()
            .Select(x => x.GetProperty("content").GetString()))).Contains("窗帘候选是米白色");
    }

    [Test]
    public async Task ReadOnlyToolsRejectScopeOverridesAndUseTheFreshClock()
    {
        var clock = new Clock(Start);
        using var app = new TestApp(clock);
        var f = await SeedAsync(app, tools: true);
        var current = await QueueAsync(app, f.Binding, "tool-args", "查一下");
        using var scope = app.Services.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<ConversationTools>();
        async Task<ConversationToolResult> Call(string name, IDictionary<string, object?> args) =>
            await tools.ExecuteAsync(new FunctionCallContent("call", name, args), f.Bot, current.Message.ConversationId, current.Message.Sequence, default);
        var empty = await Call("search_conversation_history", new Dictionary<string, object?> { ["query"] = "不存在的记录" });
        using var noMatches = JsonDocument.Parse(empty.Json);
        await Assert.That(noMatches.RootElement.GetProperty("records").GetArrayLength()).IsEqualTo(0);
        await Assert.That(noMatches.RootElement.GetProperty("note").GetString()).Contains("Do not invent");
        foreach (var args in new[]
        {
            new Dictionary<string, object?> { ["conversation_id"] = Guid.NewGuid().ToString() },
            new Dictionary<string, object?> { ["after"] = "2026-10-20" },
            new Dictionary<string, object?> { ["before_sequence"] = "invalid" }
        })
        {
            using var denied = JsonDocument.Parse((await Call("search_conversation_history", args)).Json);
            await Assert.That(denied.RootElement.TryGetProperty("error", out _)).IsTrue();
        }
        var before = await Call("get_current_time", new Dictionary<string, object?>());
        clock.Now = Start.AddMinutes(2);
        var after = await Call("get_current_time", new Dictionary<string, object?>());
        await Assert.That(after.Json).IsNotEqualTo(before.Json);
        using var time = JsonDocument.Parse(after.Json);
        await Assert.That(time.RootElement.GetProperty("currentTime").GetString()).IsEqualTo("2026-10-20T00:00:00.0000000+08:00");
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        db.Messages.AddRange(Enumerable.Range(0, 5).Select(i => new ChatMessage
        {
            BotId = f.Bot, ConversationId = current.Message.ConversationId, SessionId = current.Message.SessionId,
            SenderId = current.Message.SenderId, ExternalMessageId = "dense-" + i,
            Text = new string('旧', 600) + "关键词" + new string('后', 600), CreatedAt = Start, ExpiresAt = Start.AddDays(90)
        }));
        await db.SaveChangesAsync();
        var dense = await QueueAsync(app, f.Binding, "dense-search", "查看关键词");
        var clipped = await tools.ExecuteAsync(new FunctionCallContent("dense", "search_conversation_history",
            new Dictionary<string, object?> { ["query"] = "关键词" }), f.Bot, dense.Message.ConversationId, dense.Message.Sequence, default, maxCharacters: 900);
        await Assert.That(clipped.Json.Length).IsLessThanOrEqualTo(900);
        using var excerpts = JsonDocument.Parse(clipped.Json);
        await Assert.That(excerpts.RootElement.GetProperty("records").GetArrayLength()).IsEqualTo(1);
        var excerpt = excerpts.RootElement.GetProperty("records")[0];
        await Assert.That(excerpt.GetProperty("text").GetString()).Contains("关键词");
        await Assert.That(excerpt.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(clipped.MessageIds!).HasSingleItem();
        await Assert.That(excerpts.RootElement.GetProperty("nextBeforeSequence").GetInt64()).IsEqualTo(excerpt.GetProperty("sequence").GetInt64());
    }

    private static string Response(string text) => Json.Write(new { choices = new[] { new { message = new { content = text }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 2, completion_tokens = 2 } });
    private static async Task RunAsync(TestApp app, Guid job)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(job, default);
    }
    private static async Task<(ChatMessage Message, Guid Job)> QueueAsync(TestApp app, Guid binding, string id, string text,
        string? group = null, string sender = "111")
    {
        using var scope = app.Services.CreateScope();
        var clock = scope.ServiceProvider.GetRequiredService<ConversationClock>();
        await scope.ServiceProvider.GetRequiredService<MessageIngestor>().IngestAsync(binding,
            new NormalizedEvent("999", id, sender, group ?? sender, group is null ? ConversationKind.Private : ConversationKind.Group,
                [new MessageSegment(SegmentKind.Mention, "999"), new MessageSegment(SegmentKind.Text, text)], clock.UtcNow), default);
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var message = await db.Messages.SingleAsync(x => x.ExternalMessageId == id);
        var job = await db.Outbox.SingleAsync(x => x.Type == JobTypes.Chat && x.Payload.Contains(message.Id.ToString()));
        return (message, job.Id);
    }
    private static async Task<(Guid Bot, Guid Binding)> SeedAsync(TestApp app, bool tools = false, int contextWindow = 32768)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        var binding = new PlatformBinding { BotId = bot.Id, PlatformAccountId = "999" };
        db.Users.Add(owner); db.Bots.Add(bot); db.Bindings.Add(binding);
        db.Identities.Add(new PersonaIdentityRevision { BotId = bot.Id, Content = "你叫 Wonder", Revision = 1 });
        db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id });
        foreach (var task in new[] { TaskKind.Chat, TaskKind.RollingSummary })
            db.Models.Add(new ModelProfile { BotId = bot.Id, TaskKind = task, Endpoint = "https://model.example/v1/", Model = "test",
                SupportsTools = task == TaskKind.Chat && tools, ContextWindow = contextWindow, MaxOutputTokens = 1024,
                ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key") });
        db.GroupPolicies.AddRange(new GroupPolicy { BotId = bot.Id, GroupId = "group-one" }, new GroupPolicy { BotId = bot.Id, GroupId = "group-two" });
        await db.SaveChangesAsync();
        return (bot.Id, binding.Id);
    }
}
