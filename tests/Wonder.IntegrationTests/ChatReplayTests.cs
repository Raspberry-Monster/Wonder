using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Host.Features.Chat;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using StoredMessage = Wonder.Core.ChatMessage;

namespace Wonder.IntegrationTests;

public sealed class ChatReplayTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NextTurnPreservesActualInputAndToolProtocolAcrossMoodAndTimeChanges(bool tools)
    {
        var clock = new Clock();
        using var app = new TestApp(clock);
        var binding = await SeedAsync(app, tools);
        var first = await QueueAsync(app, binding, "first");
        if (tools) app.ModelResponses.Enqueue(ToolResponse());
        app.ModelResponses.Enqueue(Response("first answer"));
        await RunAsync(app, first.Job);
        var prior = Messages(app.ModelRequests[^1]);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            var saved = await db.ConversationReplays.SingleAsync();
            await Assert.That(saved.ThroughSequence).IsEqualTo(first.Message.Sequence);
            await Assert.That(saved.PayloadJson).DoesNotContain("test-key");
            await db.Emotions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Valence, -.9));
        }
        clock.Now = clock.Now.AddHours(2);
        var next = await QueueAsync(app, binding, "next");
        app.ModelResponses.Enqueue(Response("second answer"));
        await RunAsync(app, next.Job);
        var replayed = Messages(app.ModelRequests[^1]);
        await Assert.That(replayed.Take(prior.Length).Select(x => x.GetRawText()).ToArray())
            .IsEquivalentTo(prior.Select(x => x.GetRawText()).ToArray());
        for (var i = 0; i < prior.Length; i++)
            await Assert.That(replayed[i].GetRawText()).IsEqualTo(prior[i].GetRawText());
        await Assert.That(replayed[prior.Length].GetProperty("role").GetString()).IsEqualTo("assistant");
        await Assert.That(replayed[prior.Length].GetProperty("content").GetString()).IsEqualTo("first answer");
        await Assert.That(replayed[prior.Length + 1].GetProperty("content").GetString()).Contains("quiet and thoughtful");
        await Assert.That(app.ModelRequests.Count).IsEqualTo(tools ? 3 : 2);
        if (tools)
        {
            using var oldRequest = JsonDocument.Parse(app.ModelRequests[0]);
            using var newRequest = JsonDocument.Parse(app.ModelRequests[^1]);
            await Assert.That(newRequest.RootElement.GetProperty("tools").GetRawText())
                .IsEqualTo(oldRequest.RootElement.GetProperty("tools").GetRawText());
            var call = replayed.Single(x => x.TryGetProperty("tool_calls", out _));
            await Assert.That(call.GetProperty("reasoning_content").GetString()).IsEqualTo("private reasoning");
            await Assert.That(call.GetProperty("tool_calls")[0].GetProperty("id").GetString()).IsEqualTo("clock-call");
            await Assert.That(replayed.Count(x => x.GetProperty("role").GetString() == "tool")).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("format")]
    [Arguments("payload")]
    [Arguments("configuration")]
    [Arguments("source")]
    [Arguments("gap")]
    [Arguments("disabled")]
    public async Task InvalidCheckpointsRebuildAndTheSwitchStopsReadingAndWriting(string reason)
    {
        using var app = new TestApp();
        var binding = await SeedAsync(app);
        var first = await QueueAsync(app, binding, "first");
        app.ModelResponses.Enqueue(Response("first answer"));
        await RunAsync(app, first.Job);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var state = await db.ConversationReplays.SingleAsync();
        switch (reason)
        {
            case "format": state.FormatVersion = 999; break;
            case "payload": state.PayloadJson = "invalid-json"; break;
            case "configuration":
                await db.Models.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReasoningEffort, "high")); break;
            case "source":
                await db.Messages.Where(x => x.Id == first.Message.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "edited source")); break;
            case "gap":
                await QueueAsync(app, binding, "gap"); break;
            case "disabled":
                app.Services.GetRequiredService<IConfiguration>()["Chat:ContextReplayEnabled"] = "false"; break;
        }
        await db.SaveChangesAsync();
        var next = await QueueAsync(app, binding, "next");
        app.ModelResponses.Enqueue(Response("second answer"));
        await RunAsync(app, next.Job);
        var messages = Messages(app.ModelRequests[^1]);
        await Assert.That(messages.Count(x => Content(x).Contains("[Application context:"))).IsEqualTo(1);
        if (reason == "gap") await Assert.That(app.ModelRequests[^1]).Contains("gap");
        db.ChangeTracker.Clear();
        var saved = await db.ConversationReplays.SingleAsync();
        await Assert.That(saved.ThroughSequence).IsEqualTo(reason == "disabled" ? first.Message.Sequence : next.Message.Sequence);
        if (reason == "source") await Assert.That(app.ModelRequests[^1]).DoesNotContain("first answer");
    }

    [Test]
    public async Task ExpiryAndNoteInvalidationPhysicallyDeletePayloadAndDependencies()
    {
        using var app = new TestApp();
        var binding = await SeedAsync(app);
        var first = await QueueAsync(app, binding, "first");
        await RunAsync(app, first.Job);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var state = await db.ConversationReplays.SingleAsync();
        var note = Guid.NewGuid();
        db.ReplayDependencies.Add(new ConversationReplayDependency
            { ConversationId = state.ConversationId, Kind = "Note", ResourceId = note, Version = "1" });
        await db.SaveChangesAsync();
        await NoteSources.InvalidateAsync(db, [note], true, default);
        await Assert.That(await db.ConversationReplays.AnyAsync()).IsFalse();
        await Assert.That(await db.ReplayDependencies.AnyAsync()).IsFalse();
        var next = await QueueAsync(app, binding, "next");
        await RunAsync(app, next.Job);
        await db.ConversationReplays.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().CleanupAsync(default);
        await Assert.That(await db.ConversationReplays.AnyAsync()).IsFalse();
        await Assert.That(await db.ReplayDependencies.AnyAsync()).IsFalse();
    }

    [Test]
    public async Task FailedGenerationAndLostLeaseDoNotPublishCheckpoint()
    {
        using var app = new TestApp();
        var binding = await SeedAsync(app);
        var first = await QueueAsync(app, binding, "failed");
        app.ModelResponses.Enqueue("""{"choices":[{"finish_reason":"length","message":{"content":"partial"}}]}""");
        await Assert.That(() => RunAsync(app, first.Job)).ThrowsExactly<OutputLimitException>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.ConversationReplays.AnyAsync()).IsFalse();
        var lease = Guid.NewGuid();
        await db.Outbox.Where(x => x.Id == first.Job).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.Running)
            .SetProperty(x => x.LeaseId, lease).SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(5)));
        app.BeforeModelResponse = async _ =>
        {
            await db.Outbox.Where(x => x.Id == first.Job).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, Guid.NewGuid()));
        };
        await Assert.That(() => scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(first.Job, default, lease))
            .ThrowsExactly<DbUpdateConcurrencyException>();
        await Assert.That(await db.ConversationReplays.AnyAsync()).IsFalse();
        await Assert.That(await db.Messages.AnyAsync(x => x.Role == MessageRole.Assistant)).IsFalse();
    }

    [Test]
    [Arguments("keep")]
    [Arguments("change")]
    [Arguments("missing")]
    public async Task ImagesUseResourceReferencesAndChangedOrMissingBytesRebuild(string mode)
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var binding = await SeedAsync(app);
        var first = await QueueAsync(app, binding, "first");
        var path = Path.GetFullPath(Path.Combine(".local", "replay-image-" + Guid.NewGuid().ToString("N")));
        var bytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            var user = await db.LogicalUsers.SingleAsync(x => x.Id == first.Message.SenderId);
            user.PreferredName = "original name";
            var message = await db.Messages.SingleAsync(x => x.Id == first.Message.Id);
            message.SegmentsJson = Json.Write(new[] { new MessageSegment(SegmentKind.Text, "first"),
                new MessageSegment(SegmentKind.Image, "https://media.example/image.png") });
            var asset = new MediaAsset
            {
                BotId = message.BotId, MessageId = message.Id, Path = path, ContentType = "image/png",
                Hash = Json.Hash("https://media.example/image.png"), ContentHash = ReplayMessages.ImageHash(bytes),
                Size = bytes.Length, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            db.Media.Add(asset);
            await db.Models.ExecuteUpdateAsync(s => s.SetProperty(x => x.SupportsImages, true));
            await db.SaveChangesAsync();
            await RunAsync(app, first.Job);
            var initial = Messages(app.ModelRequests[^1]);
            var state = await db.ConversationReplays.AsNoTracking().SingleAsync();
            await Assert.That(state.PayloadJson).DoesNotContain("AQIDBA==");
            await Assert.That(state.ExpiresAt).IsLessThanOrEqualTo(asset.ExpiresAt);
            await Assert.That(await db.ReplayDependencies.AnyAsync(x => x.Kind == "Media" && x.ResourceId == asset.Id)).IsTrue();
            user.PreferredName = "current name";
            await db.SaveChangesAsync();
            if (mode == "change") await File.WriteAllBytesAsync(path, [4, 3, 2, 1]);
            if (mode == "missing") File.Delete(path);
            var next = await QueueAsync(app, binding, "next");
            await RunAsync(app, next.Job);
            var sent = Messages(app.ModelRequests[^1]);
            if (mode == "keep")
            {
                for (var i = 0; i < initial.Length; i++)
                    await Assert.That(sent[i].GetRawText()).IsEqualTo(initial[i].GetRawText());
                await Assert.That(app.ModelRequests[^1]).Contains("original name");
            }
            else await Assert.That(app.ModelRequests[^1]).DoesNotContain("AQIDBA==");
            await Assert.That(sent[^1].GetProperty("content").GetString()).Contains("current name");
        }
        finally { File.Delete(path); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CapacitySummaryRequiresARouteAndDoesNotDuplicatePendingOrRunningWork(bool route, bool running)
    {
        using var app = new TestApp();
        var binding = await SeedAsync(app);
        var first = await QueueAsync(app, binding, "first");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        for (var i = 0; i < 19; i++)
        {
            var user = new StoredMessage
            {
                BotId = first.Message.BotId, ConversationId = first.Message.ConversationId, SessionId = first.Message.SessionId,
                SenderId = first.Message.SenderId, ExternalMessageId = "history-user-" + i, Text = "history user",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            db.Messages.Add(user);
            await db.SaveChangesAsync();
            db.Messages.Add(new StoredMessage
            {
                BotId = user.BotId, ConversationId = user.ConversationId, SessionId = user.SessionId,
                ExternalMessageId = "history-answer-" + i, Text = "history answer", Role = MessageRole.Assistant,
                InReplyToSequence = user.Sequence, ExpiresAt = user.ExpiresAt,
                ContextSnapshotJson = Json.Write(new ContextSnapshot(user.BotId, user.SessionId!.Value, 1, 1, 0, 0, [], "test",
                    [user.Id], [user.SenderId!.Value], [ContentSources.Message(user)]))
            });
        }
        if (route) db.Models.Add(new ModelProfile { BotId = first.Message.BotId, TaskKind = TaskKind.RollingSummary });
        if (running)
        {
            var job = Jobs.Add(db, first.Message.BotId, JobKind.Summary,
                new SummaryJob(first.Message.SessionId!.Value, first.Message.Sequence), first.Message.ConversationId);
            job.Status = JobStatus.Running;
        }
        await db.SaveChangesAsync();
        var next = await QueueAsync(app, binding, "next");
        await RunAsync(app, next.Job);
        await Assert.That(await db.Outbox.CountAsync(x => x.Type == JobKind.Summary)).IsEqualTo(route ? 1 : 0);
        var third = await QueueAsync(app, binding, "third");
        await RunAsync(app, third.Job);
        await Assert.That(await db.Outbox.CountAsync(x => x.Type == JobKind.Summary)).IsEqualTo(route ? 1 : 0);
    }

    [Test]
    public async Task RestartAndSessionSwitchRecoverTheExactPersistedPrefix()
    {
        var path = Path.GetFullPath(Path.Combine(".local", "replay-restart-" + Guid.NewGuid().ToString("N") + ".db"));
        try
        {
            Guid binding;
            Guid? session;
            JsonElement[] prior;
            using (var initial = new TestApp(databaseFile: path))
            {
                binding = await SeedAsync(initial);
                var first = await QueueAsync(initial, binding, "first");
                session = first.Message.SessionId;
                await RunAsync(initial, first.Job);
                prior = Messages(initial.ModelRequests.Single());
                using var scope = initial.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
                await db.Sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.Closed));
            }
            using var restarted = new TestApp(databaseFile: path);
            var next = await QueueAsync(restarted, binding, "next");
            await Assert.That(next.Message.SessionId).IsNotEqualTo(session);
            await RunAsync(restarted, next.Job);
            var replayed = Messages(restarted.ModelRequests.Single());
            for (var i = 0; i < prior.Length; i++)
                await Assert.That(replayed[i].GetRawText()).IsEqualTo(prior[i].GetRawText());
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task MemoryRevocationPurgesPersistedCopiesBeforeTheNextChat()
    {
        using var app = new TestApp();
        var binding = await SeedAsync(app);
        var first = await QueueAsync(app, binding, "first");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var user = await db.LogicalUsers.SingleAsync(x => x.Id == first.Message.SenderId);
        user.AllowMemory = true;
        var memory = new Memory { BotId = first.Message.BotId, SubjectId = user.Id, Text = "private memory secret" };
        db.Memories.Add(memory);
        await db.SaveChangesAsync();
        await RunAsync(app, first.Job);
        await Assert.That((await db.ConversationReplays.SingleAsync()).PayloadJson).Contains(memory.Text);
        await scope.ServiceProvider.GetRequiredService<MemoryService>().ArchiveAsync(user.Id, memory.Id, 1, "archive", default);
        await Assert.That(await db.ConversationReplays.AnyAsync()).IsFalse();
        await Assert.That(await db.ReplayDependencies.AnyAsync()).IsFalse();
        var next = await QueueAsync(app, binding, "next");
        await RunAsync(app, next.Job);
        await Assert.That(app.ModelRequests[^1]).DoesNotContain("private memory secret");
    }

    [Test]
    public async Task CheckpointMatchesTheLastActualRequestAfterToolBudgetTrimming()
    {
        using var app = new TestApp();
        var binding = await SeedAsync(app, true, 20000);
        var first = await QueueAsync(app, binding, "bootstrap");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var old = new StoredMessage
        {
            BotId = first.Message.BotId, ConversationId = first.Message.ConversationId, SessionId = first.Message.SessionId,
            SenderId = first.Message.SenderId, ExternalMessageId = "old", Text = "old-history-marker" + new string('h', 7000),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Messages.Add(old);
        await db.SaveChangesAsync();
        db.Messages.Add(new StoredMessage
        {
            BotId = old.BotId, ConversationId = old.ConversationId, SessionId = old.SessionId, Role = MessageRole.Assistant,
            InReplyToSequence = old.Sequence, ExternalMessageId = "legacy-answer", Text = "legacy answer", ExpiresAt = old.ExpiresAt,
            ContextSnapshotJson = Json.Write(new ContextSnapshot(old.BotId, old.SessionId!.Value, 1, 1, 0, 0, [], "test",
                [old.Id], [old.SenderId!.Value], [ContentSources.Message(old)]))
        });
        await db.SaveChangesAsync();
        var current = await QueueAsync(app, binding, "ask-tools");
        app.ModelResponses.Enqueue(ToolResponse().Replace("private reasoning", new string('r', 10000), StringComparison.Ordinal));
        app.ModelResponses.Enqueue(Response("final answer"));
        await RunAsync(app, current.Job);
        await Assert.That(app.ModelRequests[0]).Contains("old-history-marker");
        await Assert.That(app.ModelRequests[1]).DoesNotContain("old-history-marker");
        var state = await db.ConversationReplays.AsNoTracking().SingleAsync();
        await Assert.That(state.PayloadJson).DoesNotContain("old-history-marker");
        var payload = Json.Read<ReplayPayload>(state.PayloadJson);
        var restored = new List<Microsoft.Extensions.AI.ChatMessage>();
        foreach (var dto in payload.Messages.SkipLast(1))
            restored.Add(await ReplayMessages.DecodeAsync(dto, _ => throw new InvalidOperationException()));
        var router = scope.ServiceProvider.GetRequiredService<ModelRouter>();
        var profile = await router.ProfileAsync(old.BotId, TaskKind.Chat, default);
        await router.GenerateAsync(profile, restored, false, default, ConversationTools.Definitions.ToList());
        var original = Messages(app.ModelRequests[1]);
        var recovered = Messages(app.ModelRequests[2]);
        await Assert.That(recovered.Length).IsEqualTo(original.Length);
        for (var i = 0; i < original.Length; i++)
            await Assert.That(recovered[i].GetRawText()).IsEqualTo(original[i].GetRawText());
    }

    internal static JsonElement[] Messages(string request)
    {
        using var document = JsonDocument.Parse(request);
        return document.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.Clone()).ToArray();
    }
    private static string Content(JsonElement message) => message.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.String ? content.GetString()! : "";

    internal static string Response(string text) => Json.Write(new
    {
        choices = new[] { new { finish_reason = "stop", message = new { content = text } } },
        usage = new { prompt_tokens = 12, completion_tokens = 4 }
    });
    internal static string ToolResponse() => """
        {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"reasoning_content":"private reasoning",
        "tool_calls":[{"id":"clock-call","type":"function","function":{"name":"get_current_time","arguments":"{ }"}}]}}]}
        """;

    internal static async Task RunAsync(TestApp app, Guid job)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(job, default);
    }
    internal static async Task<(StoredMessage Message, Guid Job)> QueueAsync(TestApp app, Guid binding, string id)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MessageIngestor>().IngestAsync(binding,
            new NormalizedEvent("999", id, "111", "111", ConversationKind.Private,
                [new MessageSegment(SegmentKind.Text, id)], scope.ServiceProvider.GetRequiredService<ConversationClock>().UtcNow), default);
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var message = await db.Messages.SingleAsync(x => x.ExternalMessageId == id);
        var job = await db.Outbox.SingleAsync(x => x.Type == JobKind.Chat && x.Payload.Contains(message.Id.ToString()));
        return (message, job.Id);
    }
    internal static async Task<Guid> SeedAsync(TestApp app, bool tools = false, int window = 65536)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        var binding = new PlatformBinding { BotId = bot.Id, PlatformAccountId = "999" };
        db.Users.Add(owner);
        db.Bots.Add(bot);
        db.Bindings.Add(binding);
        db.Identities.Add(new PersonaIdentityRevision { BotId = bot.Id, Content = "Wonder", Revision = 1 });
        db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id });
        db.Models.Add(new ModelProfile
        {
            BotId = bot.Id, TaskKind = TaskKind.Chat, Endpoint = "https://model.example/v1/", Model = "test",
            TokenizerId = "estimate", SupportsTools = tools, ContextWindow = window, MaxOutputTokens = 1024,
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key")
        });
        await db.SaveChangesAsync();
        return binding.Id;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
