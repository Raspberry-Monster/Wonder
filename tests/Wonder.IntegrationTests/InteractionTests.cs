using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;
using ChatMessage = Wonder.Core.ChatMessage;

namespace Wonder.IntegrationTests;

public sealed class InteractionTests
{
    [Test]
    public async Task OnlyEnglishEndClosesTheCurrentSegment()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        await IngestAsync(app, f.Binding, "ordinary-end-text", "/结束");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var session = await db.Sessions.AsNoTracking().SingleAsync();
        await Assert.That(session.DeadlineAt).IsGreaterThan(DateTimeOffset.UtcNow);
        await Assert.That((await db.Messages.SingleAsync(x => x.ExternalMessageId == "ordinary-end-text")).Text).IsEqualTo("/结束");
        await IngestAsync(app, f.Binding, "end-command", "/end");
        var ended = await db.Sessions.AsNoTracking().SingleAsync();
        await Assert.That(ended.DeadlineAt).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task HelpIsDurableDeduplicatedAndDoesNotEnterAnyAgentSourceEvenWithoutPersistence()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app, false);
        await IngestAsync(app, f.Binding, "help", "/help");
        await IngestAsync(app, f.Binding, "help", "/help");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var marker = await db.Messages.SingleAsync();
        await Assert.That(marker.ContextInvalidated).IsTrue();
        await Assert.That(marker.Text).IsEmpty();
        await Assert.That(marker.SessionId).IsNull();
        await Assert.That(await db.Sessions.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.Outbox.CountAsync(x => x.Type != JobKind.Outgoing)).IsEqualTo(0);
        var text = string.Concat(await db.Parts.OrderBy(x => x.Sequence).Select(x => x.Text).ToArrayAsync());
        foreach (var command in new[] { "/help", "/bind", "/end", "/force" })
            await Assert.That(text).Contains(command);
        await Assert.That(text).DoesNotContain("/结束");
        await scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync((await db.Outbox.SingleAsync()).Id, default);
        await Assert.That(app.Sender.Sent).IsNotEmpty();
        await Assert.That(app.ModelRequests).IsEmpty();
    }

    [Test]
    public async Task MuteBlocksAllOriginalContentButAllowsOldOutputAndForceAcrossClosedSessions()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "first", "你好");
        await Assert.That(await CallAsync(app, e, 0, "interaction_mute", new() { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") })).Contains("Accepted");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var session = await db.Sessions.SingleAsync();
        var deadline = session.DeadlineAt;
        session.Status = SessionStatus.Closed;
        await db.SaveChangesAsync();
        await IngestAsync(app, f.Binding, "blocked", "/end 我的秘密不应被读到");
        db.ChangeTracker.Clear();
        var blocked = await db.Messages.SingleAsync(x => x.ExternalMessageId == "blocked");
        await Assert.That(blocked.Text).IsEmpty();
        await Assert.That(blocked.SegmentsJson).IsEqualTo("[]");
        await Assert.That(blocked.AllowReflection).IsFalse();
        await Assert.That((await db.Sessions.SingleAsync()).DeadlineAt).IsEqualTo(deadline);
        await IngestAsync(app, f.Binding, "force", "/force  请完整传递\n保留所有文字  ");
        var forced = await db.Messages.SingleAsync(x => x.ExternalMessageId == "force");
        await Assert.That(forced.Text).IsEqualTo("/force  请完整传递\n保留所有文字  ");
        await Assert.That(forced.SessionId).IsNotEqualTo(session.Id);
        await Assert.That(forced.AllowReflection).IsTrue();
        await Assert.That((await db.ForceReceipts.SingleAsync()).RemainingAfter).IsEqualTo(2);
        await Assert.That(await ConversationHistory.Visible(db, f.Bot, blocked.ConversationId, long.MaxValue,
            DateTimeOffset.UtcNow).AnyAsync(x => x.Id == blocked.Id)).IsFalse();
        // A legitimately admitted pre-mute reply remains deliverable.
        var original = await db.Outbox.SingleAsync(x => x.Id == e.ChatJobId);
        var originalMessage = await db.Messages.AsNoTracking().SingleAsync(x => x.Id == e.MessageId);
        ChatWorkflow.QueueReply(db, original, session.Id, "我仍然可以发消息", new ContextSnapshot(f.Bot, session.Id, 1, 0, 0, 0,
            [], "test", [originalMessage.Id], [originalMessage.SenderId!.Value], [ContentSources.Message(originalMessage)]));
        await db.SaveChangesAsync();
        var batch = await db.Batches.SingleAsync(x => x.SourceJobId == original.Id);
        var outgoing = await db.Outbox.SingleAsync(x => x.Type == JobKind.Outgoing && x.Payload.Contains(batch.Id.ToString()));
        await scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(outgoing.Id, default);
        await Assert.That(app.Sender.Sent).Contains("我仍然可以发消息");
    }

    [Test]
    public async Task CommittedUnmuteReplaysBeforeMuteStateAndRejectsArgumentDriftRevocationAndStaleLease()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var start = await TurnAsync(app, f.Binding, "mute", "安静一下");
        await CallAsync(app, start, 0, "interaction_mute", new() { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        var e = await TurnAsync(app, f.Binding, "force", "/force 我在");
        var first = await CallAsync(app, e, 0, "interaction_unmute");
        await Assert.That(first).Contains("Accepted");
        var replay = await CallAsync(app, e, 0, "interaction_unmute");
        await Assert.That(replay).IsEqualTo(first);
        var replacementLease = Guid.NewGuid();
        using (var renewed = app.Services.CreateScope())
            await renewed.ServiceProvider.GetRequiredService<WonderDbContext>().Outbox.Where(x => x.Id == e.ChatJobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, replacementLease));
        e = e with { LeaseId = replacementLease };
        await Assert.That(await CallAsync(app, e, 0, "interaction_unmute")).IsEqualTo(first);
        await Assert.That(await CallAsync(app, e, 1, "interaction_reset_force")).Contains("AmbiguousRetry");
        await Assert.That(await CallAsync(app, e, 0, "interaction_reset_force")).Contains("OperationConflict");
        await Assert.That(await CallAsync(app, e with { LeaseId = Guid.NewGuid() }, 0, "interaction_unmute")).Contains("StaleExecution");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.LogicalUsers.Where(x => x.Id == e.UserId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevocationEpoch, 1));
        await Assert.That(await CallAsync(app, e, 0, "interaction_unmute")).Contains("AuthorizationChanged");
        await Assert.That(await db.AgentOperations.CountAsync(x => x.ChatJobId == e.ChatJobId)).IsEqualTo(1);
    }

    [Test]
    public async Task NoReplyEndsToolLoopAndSurvivesRetryWithoutAnotherModelRequestOrOutgoing()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var start = await TurnAsync(app, f.Binding, "mute", "安静一下");
        await CallAsync(app, start, 0, "interaction_mute", new() { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        var e = await TurnAsync(app, f.Binding, "force", "/force 我在");
        app.ModelResponses.Enqueue(Json.Write(new
        {
            choices = new[] { new { finish_reason = "tool_calls", message = new { content = "这段文字不能发出",
                tool_calls = new[] { new { id = "vendor-random", type = "function", function = new { name = "interaction_no_reply", arguments = "{}" } } } } } },
            usage = new { prompt_tokens = 2, completion_tokens = 2 }
        }));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var workflow = scope.ServiceProvider.GetRequiredService<ChatWorkflow>();
        await workflow.RunAsync(e.ChatJobId, default, e.LeaseId);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(1);
        await Assert.That(await db.ChatDispositions.AnyAsync(x => x.ChatJobId == e.ChatJobId)).IsTrue();
        await Assert.That(await db.Batches.AnyAsync(x => x.SourceJobId == e.ChatJobId)).IsFalse();
        await Assert.That(await db.Messages.AnyAsync(x => x.ExternalMessageId == "generated:" + e.ChatJobId)).IsFalse();
        // Simulate crash after disposition, before Job/Inbox completion.
        await db.Inbox.Where(x => x.EventId == e.ChatJobId).ExecuteDeleteAsync();
        await db.Outbox.Where(x => x.Id == e.ChatJobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.Running)
            .SetProperty(x => x.LeaseId, e.LeaseId).SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(10)));
        await workflow.RunAsync(e.ChatJobId, default, e.LeaseId);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(1);
        await Assert.That(await db.Inbox.AnyAsync(x => x.EventId == e.ChatJobId)).IsTrue();
    }

    [Test]
    public async Task ResetSupersedesOldRefundsAndDoesNotUnmute()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var start = await TurnAsync(app, f.Binding, "mute", "安静一下");
        await CallAsync(app, start, 0, "interaction_mute", new() { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        var old = await TurnAsync(app, f.Binding, "old", "/force 第一代");
        var reset = await TurnAsync(app, f.Binding, "reset", "/force 再给机会");
        await CallAsync(app, reset, 0, "interaction_reset_force");
        await IngestAsync(app, f.Binding, "new", "/force 新一代");
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<InteractionBoundary>();
        await scope.ServiceProvider.GetRequiredService<WonderDbContext>().Outbox.Where(x => x.Id == old.ChatJobId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.DeadLetter));
        await service.CompensateAsync(old.ChatJobId, default);
        await service.CompensateAsync(old.ChatJobId, default);
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var mute = await db.Mutes.SingleAsync();
        await Assert.That(mute.ForceRemaining).IsEqualTo(2);
        await Assert.That(mute.ResetGeneration).IsEqualTo(1);
        await Assert.That(mute.EndedAt).IsNull();
        await Assert.That((await db.ForceReceipts.SingleAsync(x => x.ChatJobId == old.ChatJobId)).CompensationStatus).IsEqualTo(ForceCompensationOutcome.SupersededByReset);
    }

    [Test]
    public async Task PostgreSqlConcurrentLastForceAndDuplicateEventsChargeExactlyOnce()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await SeedAsync(app);
        var start = await TurnAsync(app, f.Binding, "mute", "安静一下");
        await CallAsync(app, start, 0, "interaction_mute", new() { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.Mutes.ExecuteUpdateAsync(s => s.SetProperty(x => x.ForceRemaining, 1));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => IngestAsync(app, f.Binding, "race-" + i, "/force 最后一次")));
        var receipt = await db.ForceReceipts.SingleAsync();
        await IngestAsync(app, f.Binding, receipt.PlatformEventKey, "/force 最后一次");
        await Assert.That(await db.ForceReceipts.CountAsync()).IsEqualTo(1);
        await Assert.That((await db.Mutes.SingleAsync()).ForceRemaining).IsEqualTo(0);
        await Assert.That(await db.Messages.CountAsync(x => x.Id == receipt.MessageId && x.SessionId != null)).IsEqualTo(1);
        await Assert.That(await db.Outbox.CountAsync(x => x.Id == receipt.ChatJobId && x.Type == JobKind.Chat)).IsEqualTo(1);
    }

    [Test]
    public async Task OversizedForceIsRejectedBeforeChargingAndExpiredMuteDoesNotNeedASweep()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "mute", "安静一下");
        await CallAsync(app, e, 0, "interaction_mute", new() { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        await IngestAsync(app, f.Binding, "too-long", "/force " + new string('长', 40000));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.ForceReceipts.CountAsync()).IsEqualTo(0);
        await Assert.That((await db.Mutes.SingleAsync()).ForceRemaining).IsEqualTo(3);
        await db.Mutes.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await IngestAsync(app, f.Binding, "after-expiry", "到期后普通消息");
        await Assert.That((await db.Messages.SingleAsync(x => x.ExternalMessageId == "after-expiry")).ShouldRespond).IsTrue();
    }

    [Test]
    public async Task SweepRecoversAnUnprocessedRefundAndRetryCannotDeliverTheRefundedForce()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "mute", "安静一下");
        await CallAsync(app, e, 0, "interaction_mute", new() { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        var failed = await TurnAsync(app, f.Binding, "failed-force", "/force 未能开始处理");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.Outbox.Where(x => x.Id == failed.ChatJobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.DeadLetter));
        var maintenance = scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>();
        await maintenance.SweepSessionsAsync(default); await maintenance.SweepSessionsAsync(default);
        await Assert.That((await db.Mutes.AsNoTracking().SingleAsync()).ForceRemaining).IsEqualTo(3);
        await Assert.That((await db.ForceReceipts.AsNoTracking().SingleAsync()).CompensationStatus).IsEqualTo(ForceCompensationOutcome.Refunded);
        await db.Outbox.Where(x => x.Id == failed.ChatJobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.Pending));
        await scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(failed.ChatJobId, default);
        await Assert.That(app.ModelRequests).IsEmpty();
    }

    internal static Task<string> CallAsync(TestApp app, AgentExecution e, int slot, string name,
        Dictionary<string, object?>? args = null) => app.Services.GetRequiredService<InteractionBoundary>()
        .ExecuteAsync(e, slot, new FunctionCallContent(Guid.NewGuid().ToString(), name, args ?? new()), default);

    internal static Task IngestAsync(TestApp app, Guid binding, string id, string text) =>
        app.Services.GetRequiredService<MessageIngestor>().IngestAsync(binding,
            new NormalizedEvent("999", id, "111", "111", ConversationKind.Private,
                [new MessageSegment(SegmentKind.Text, text)], DateTimeOffset.UtcNow), default);

    internal static async Task<AgentExecution> TurnAsync(TestApp app, Guid binding, string id, string text)
    {
        await IngestAsync(app, binding, id, text);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var message = await db.Messages.SingleAsync(x => x.ExternalMessageId == id);
        var job = await db.Outbox.SingleAsync(x => x.Type == JobKind.Chat && x.Payload.Contains(message.Id.ToString()));
        job.Status = JobStatus.Running;
        job.LeaseId = Guid.NewGuid();
        job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await db.SaveChangesAsync();
        return new AgentExecution(job.Id, job.LeaseId.Value, job.BotId, message.ConversationId,
            message.SenderId!.Value, message.Id, 0, 1);
    }

    internal static async Task<(Guid Bot, Guid Binding)> SeedAsync(TestApp app, bool persist = true)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var user = new LogicalUser { AllowMemory = true };
        var owner = new ApplicationUser { Id = Guid.NewGuid(), LogicalUserId = user.Id };
        var bot = new BotInstance { OwnerId = owner.Id, PersistMessages = persist };
        var binding = new PlatformBinding { BotId = bot.Id, PlatformAccountId = "999" };
        db.LogicalUsers.Add(user);
        db.PlatformIdentities.Add(new PlatformIdentity { UserId = user.Id, ExternalId = "111" });
        db.Users.Add(owner); db.Bots.Add(bot); db.Bindings.Add(binding);
        db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id });
        db.Identities.Add(new PersonaIdentityRevision { BotId = bot.Id, Content = "你是 Miku", Revision = 1 });
        db.Models.Add(new ModelProfile
        {
            BotId = bot.Id, TaskKind = TaskKind.Chat, Endpoint = "https://model.example/v1/", Model = "test",
            SupportsTools = true, ContextWindow = 32768, MaxOutputTokens = 1024,
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key")
        });
        await db.SaveChangesAsync();
        return (bot.Id, binding.Id);
    }
}
