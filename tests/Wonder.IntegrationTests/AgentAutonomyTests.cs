using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Host.Controllers;
using Wonder.Infrastructure;
using static Wonder.IntegrationTests.InteractionTests;

namespace Wonder.IntegrationTests;

public sealed class AgentAutonomyTests
{
    [Test]
    public async Task OnlyTheSubjectCanClearAReflectionReasonWithoutChangingQuotaOrWork()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "reason", "需要整理");
        await CallAsync(app, e, 0, "reflection_request_review", new() { ["priority"] = "Normal", ["reason"] = "私人整理原因" });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var run = await db.ReflectionGenerations.SingleAsync();
        var other = new LogicalUser();
        var owner = new ApplicationUser { Id = Guid.NewGuid(), LogicalUserId = other.Id };
        db.LogicalUsers.Add(other); db.Users.Add(owner);
        (await db.Bots.SingleAsync()).OwnerId = owner.Id;
        await db.SaveChangesAsync();
        var wrongIdentity = await ControllerAsync(db, app, other.Id);
        var ownerController = new ReflectionRunsController(db) { ControllerContext = wrongIdentity.ControllerContext };
        await Assert.That(await ownerController.ClearReason(run.Id, default) is NotFoundResult).IsTrue();
        await Assert.That((await db.AgentReflectionRequests.SingleAsync()).Reason).IsEqualTo("私人整理原因");
        var identity = await ControllerAsync(db, app, e.UserId);
        var controller = new ReflectionRunsController(db) { ControllerContext = identity.ControllerContext };
        await Assert.That(await controller.ClearReason(run.Id, default) is NoContentResult).IsTrue();
        await Assert.That((await db.AgentReflectionRequests.AsNoTracking().SingleAsync()).Reason).IsEmpty();
        await Assert.That((await db.AgentReflectionQuotas.SingleAsync()).UsedNormal).IsEqualTo(1);
        await Assert.That((await db.Outbox.SingleAsync(x => x.Id == run.JobId)).Status).IsEqualTo(JobStatus.Pending);
    }
    [Test]
    public async Task PostgreSqlConcurrentNoteOperationsReplayBeforeOldVersionOrSettingsChecks()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "note-race", "我的观察");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.LogicalUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowNotes, true));
        var creates = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CallAsync(app, e, 0, "notes_create",
            new() { ["content"] = "观察：用户说今天赶时间" })));
        await Assert.That(creates.Distinct().Count()).IsEqualTo(1);
        var note = await db.Notes.AsNoTracking().SingleAsync();
        var args = new Dictionary<string, object?> { ["note_id"] = note.Id.ToString(), ["expected_version"] = 1,
            ["content"] = "更正后的观察", ["reason"] = "新的说明" };
        var updates = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CallAsync(app, e, 1, "notes_update", args)));
        await Assert.That(updates.Distinct().Count()).IsEqualTo(1);
        await Assert.That((await db.Notes.AsNoTracking().SingleAsync()).Version).IsEqualTo(2);
        await Assert.That(await db.NoteRevisions.CountAsync()).IsEqualTo(2);
        await db.Bots.ExecuteUpdateAsync(s => s.SetProperty(x => x.SettingsRevision, 2));
        await Assert.That(await CallAsync(app, e, 1, "notes_update", args)).IsEqualTo(updates[0]);
        await Assert.That(await CallAsync(app, e, 2, "notes_create", new() { ["content"] = "旧配置不能新写" })).Contains("AuthorizationChanged");
        await db.LogicalUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowNotes, false).SetProperty(x => x.NotesEpoch, 1));
        await Assert.That(await CallAsync(app, e with { SettingsRevision = 2 }, 2, "notes_create", new() { ["content"] = "撤销后不能新写" })).Contains("NotesAuthorizationRequired");
        await Assert.That(await db.Notes.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task NoteCorrectionImmediatelyRetiresDerivedHistorySummaryAndPendingOutputAndReplaysOldVersion()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "note", "只是赶时间");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.LogicalUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowNotes, true));
        var created = await CallAsync(app, e, 0, "notes_create", new() { ["content"] = "猜测：他不想聊天", ["claim_kind"] = "guess" });
        var noteId = JsonDocument.Parse(created).RootElement.GetProperty("noteId").GetGuid();
        await CallAsync(app, e, 1, "notes_read", new() { ["note_id"] = noteId.ToString() });
        var message = await db.Messages.SingleAsync(x => x.Id == e.MessageId);
        var derived = new ChatMessage { BotId = e.BotId, ConversationId = e.ConversationId,
            SessionId = message.SessionId, Role = MessageRole.Assistant, ExternalMessageId = "generated:" + e.ChatJobId,
            Text = "旧推测", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.Messages.Add(derived);
        await NoteSources.CopyChatAsync(db, e.ChatJobId, derived.Id, default);
        var summary = new ConversationSummary { BotId = e.BotId, SessionId = message.SessionId!.Value, Text = "旧摘要" };
        db.Summaries.Add(summary);
        await NoteSources.AddAsync(db, NoteDependencyTargetKind.Summary, summary.Id, noteId, 1, default);
        ChatWorkflow.QueueReply(db, await db.Outbox.SingleAsync(x => x.Id == e.ChatJobId), message.SessionId.Value, "旧推测");
        await db.SaveChangesAsync();
        var args = new Dictionary<string, object?> { ["note_id"] = noteId.ToString(), ["expected_version"] = 1,
            ["content"] = "观察：用户解释只是赶时间，旧猜测已纠正", ["reason"] = "用户明确解释" };
        var updated = await CallAsync(app, e, 2, "notes_update", args);
        await Assert.That(updated).Contains("Accepted");
        await Assert.That(await CallAsync(app, e, 2, "notes_update", args)).IsEqualTo(updated);
        await Assert.That((await db.Messages.AsNoTracking().SingleAsync(x => x.Id == derived.Id)).ContextInvalidated).IsTrue();
        await Assert.That((await db.Summaries.AsNoTracking().SingleAsync()).ContextInvalidated).IsTrue();
        await Assert.That(await db.Parts.AllAsync(x => x.Status == DeliveryStatus.Failed)).IsTrue();
        await Assert.That(await db.NoteRevisions.CountAsync()).IsEqualTo(2);
        var refreshed = await TurnAsync(app, f.Binding, "fresh-note-context", "查看新判断");
        var search = await CallAsync(app, refreshed, 0, "notes_search", new() { ["query"] = "" });
        await Assert.That(search).Contains("只是赶时间");
        await Assert.That(search).DoesNotContain("他不想聊天");
        await Assert.That(search).Contains("outdated");
        await db.LogicalUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowNotes, false).SetProperty(x => x.NotesEpoch, 1));
        await Assert.That(await CallAsync(app, refreshed, 1, "notes_create", new() { ["content"] = "不能落库" })).DoesNotContain("Accepted");
        await Assert.That(await db.Notes.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task UserNotesCannotBeChangedByAgentAndAgentNotesAreNotListedToUsers()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "note", "记录");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.LogicalUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowNotes, true));
        var userNote = new Note { BotId = e.BotId, UserId = e.UserId, ConversationId = e.ConversationId, AuthorKind = NoteAuthorKind.User, Content = "用户自己的笔记" };
        db.Notes.Add(userNote); await db.SaveChangesAsync();
        await CallAsync(app, e, 0, "notes_create", new() { ["content"] = "Miku 私人观察" });
        await Assert.That(await CallAsync(app, e, 1, "notes_update", new() { ["note_id"] = userNote.Id.ToString(),
            ["expected_version"] = 1, ["content"] = "假冒用户修改", ["reason"] = "越权" })).Contains("NoteNotFound");
        var controller = await ControllerAsync(db, app, e.UserId);
        var response = (OkObjectResult)await controller.UserNotes(default);
        using var json = JsonDocument.Parse(Json.Write(response.Value));
        await Assert.That(json.RootElement.GetArrayLength()).IsEqualTo(1);
        await Assert.That(json.RootElement[0].GetProperty("content").GetString()).IsEqualTo("用户自己的笔记");
    }

    [Test]
    public async Task PostgreSqlNotesPrivacyDeletionErasesVersionsAndDependencies()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "note", "个人内容");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.LogicalUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowNotes, true));
        await CallAsync(app, e, 0, "notes_create", new() { ["content"] = "不可恢复的私密内容" });
        var note = await db.Notes.AsNoTracking().SingleAsync();
        await CallAsync(app, e, 1, "notes_read", new() { ["note_id"] = note.Id.ToString() });
        var input = await db.Messages.SingleAsync(x => x.Id == e.MessageId);
        var derived = new ChatMessage { BotId = e.BotId, ConversationId = e.ConversationId, SessionId = input.SessionId,
            Role = MessageRole.Assistant, ExternalMessageId = "generated:" + e.ChatJobId, Text = note.Content, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.Messages.Add(derived);
        await NoteSources.CopyChatAsync(db, e.ChatJobId, derived.Id, default);
        var summary = new ConversationSummary { BotId = e.BotId, SessionId = input.SessionId!.Value, Text = note.Content };
        db.Summaries.Add(summary); await NoteSources.AddAsync(db, NoteDependencyTargetKind.Summary, summary.Id, note.Id, note.Version, default);
        ChatWorkflow.QueueReply(db, await db.Outbox.SingleAsync(x => x.Id == e.ChatJobId), input.SessionId.Value, note.Content);
        await db.SaveChangesAsync();
        var controller = await ControllerAsync(db, app, e.UserId);
        await controller.DeletePersonalData(default);
        db.ChangeTracker.Clear();
        var ledger = await db.Revocations.AsNoTracking().ToArrayAsync();
        await Assert.That(ledger.Single().ResourceKind).IsEqualTo(DeletionResourceKind.Note);
        await Assert.That((await db.Notes.SingleAsync()).Content).IsEmpty();
        await Assert.That((await db.Messages.AsNoTracking().SingleAsync(x => x.Id == derived.Id)).Text).IsEmpty();
        await Assert.That((await db.Summaries.AsNoTracking().SingleAsync()).ContextInvalidated).IsTrue();
        await Assert.That(await db.Parts.AllAsync(x => x.Status == DeliveryStatus.Failed && x.Text == "")).IsTrue();
        await Assert.That(await db.NoteRevisions.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task ReflectionQuotaUsesTwoNormalAndOneImportantWithoutEndingSessionOrChargingReplays()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        for (var i = 0; i < 4; i++)
        {
            var e = await TurnAsync(app, f.Binding, "review-" + i, "新来源 " + i);
            var args = new Dictionary<string, object?> { ["priority"] = i == 3 ? "Important" : "Normal", ["reason"] = "整理当前对话" };
            var result = await CallAsync(app, e, 0, "reflection_request_review", args);
            await Assert.That(result).Contains(i == 2 ? "QuotaExhausted" : "Accepted");
            if (i != 2) await Assert.That(await CallAsync(app, e, 0, "reflection_request_review", args)).IsEqualTo(result);
        }
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var quota = await db.AgentReflectionQuotas.SingleAsync();
        await Assert.That(quota.UsedNormal).IsEqualTo(2);
        await Assert.That(quota.UsedImportant).IsEqualTo(1);
        await Assert.That(await db.ReflectionGenerations.CountAsync()).IsEqualTo(3);
        await Assert.That((await db.Sessions.SingleAsync()).Status).IsEqualTo(SessionStatus.Active);
    }

    [Test]
    public async Task PostgreSqlEquivalentUserSourcesShareOneCanonicalRunDespiteLaterAssistantBoundary()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = pg.CreateApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "source", "我喜欢蓝色");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var input = await db.Messages.SingleAsync(x => x.Id == e.MessageId);
        var assistant = new ChatMessage { BotId = e.BotId, ConversationId = e.ConversationId, SessionId = input.SessionId,
            Role = MessageRole.Assistant, ExternalMessageId = "later-assistant", Text = "知道啦", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.Messages.Add(assistant); await db.SaveChangesAsync();
        async Task AutoFull()
        {
            await using var concurrent = await app.Services.GetRequiredService<IDbContextFactory<WonderDbContext>>().CreateDbContextAsync();
            await using var admission = await concurrent.Database.BeginTransactionAsync();
            await UserFence.CheckAsync(concurrent, e.UserId, 0, default);
            await ReflectionRuns.AdmitAsync(concurrent, e.BotId, e.ConversationId,
                new ReflectionJob(input.SessionId!.Value, e.UserId, assistant.Sequence, 0, ReflectionKind.Full), default);
            await concurrent.SaveChangesAsync(); await admission.CommitAsync();
        }
        await Task.WhenAll(AutoFull(), CallAsync(app, e, 0, "reflection_request_review",
            new() { ["priority"] = "Normal", ["reason"] = "整理" }));
        await using var tx = await db.Database.BeginTransactionAsync();
        var (_, status) = await ReflectionRuns.AdmitAsync(db, e.BotId, e.ConversationId,
            new ReflectionJob(input.SessionId!.Value, e.UserId, assistant.Sequence, 0, ReflectionKind.Full), default);
        await Assert.That(status).IsEqualTo(ReflectionAdmissionOutcome.AlreadyScheduled);
        await db.SaveChangesAsync(); await tx.CommitAsync();
        await Assert.That(await db.ReflectionGenerations.CountAsync()).IsEqualTo(1);
        await Assert.That(await db.Outbox.CountAsync(x => x.Type == JobKind.Reflection && x.Payload.Contains("full"))).IsEqualTo(1);
    }

    [Test]
    public async Task CanonicalReflectionPublishesOneResultAndThreeConsumersAndCancelsRevokedSources()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app);
        var e = await TurnAsync(app, f.Binding, "source", "我喜欢蓝色");
        await CallAsync(app, e, 0, "reflection_request_review", new() { ["priority"] = "Normal", ["reason"] = "整理" });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        db.Models.Add(new ModelProfile { BotId = f.Bot, TaskKind = TaskKind.LightReflection,
            Endpoint = "https://model.example/v1/", Model = "test", ContextWindow = 8192, MaxOutputTokens = 1024,
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key") });
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(Json.Write(new { choices = new[] { new { message = new { content =
            Json.Write(new ReflectionProposal(false, null, [], [])) }, finish_reason = "stop" } }, usage = new { prompt_tokens = 2, completion_tokens = 2 } }));
        var run = await db.ReflectionGenerations.AsNoTracking().SingleAsync();
        var workflow = scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>();
        await workflow.GenerateAsync(run.JobId, default);
        await workflow.GenerateAsync(run.JobId, default);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(1);
        await Assert.That(await db.Reflections.CountAsync()).IsEqualTo(1);
        await Assert.That(await db.Outbox.CountAsync(x => x.CausationId == run.JobId)).IsEqualTo(3);
        var result = await db.Reflections.SingleAsync();
        db.MemoryCandidates.Add(new MemoryMaintenanceCandidate { BotId = f.Bot, SubjectId = e.UserId,
            SourceReflectionId = result.Id,
            Text = "等待维护", FactKey = "pending", CandidateHash = "test-pending" });
        await db.SaveChangesAsync();
        using var history = JsonDocument.Parse(Json.Write(await ReflectionRunHistory.ReadAsync(db, e.UserId, f.Bot, 0, 10, default)));
        await Assert.That(history.RootElement[0].GetProperty("status").GetString()).IsEqualTo("ReflectionCompleted");
        await Assert.That(history.RootElement[0].GetProperty("memoryCandidateCount").GetInt32()).IsEqualTo(1);
        await Assert.That(await ReflectionRunHistory.ReadAsync(db, Guid.NewGuid(), f.Bot, 0, 10, default)).IsEmpty();
        var later = await TurnAsync(app, f.Binding, "later", "我还有新的偏好");
        await CallAsync(app, later, 0, "reflection_request_review", new() { ["priority"] = "Important", ["reason"] = "新来源" });
        var pending = await db.ReflectionGenerations.AsNoTracking().SingleAsync(x => x.Id != run.Id);
        await db.LogicalUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowMemory, false).SetProperty(x => x.RevocationEpoch, 1));
        await workflow.GenerateAsync(pending.JobId, default);
        await Assert.That((await db.ReflectionGenerations.AsNoTracking().SingleAsync(x => x.Id == pending.Id)).Status).IsEqualTo(ReflectionGenerationStatus.Cancelled);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(1);
    }

    private static async Task<NotesController> ControllerAsync(WonderDbContext db, TestApp app, Guid user)
    {
        var account = await db.Users.SingleAsync(x => x.LogicalUserId == user);
        return new NotesController(db)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.Id.ToString())], "test")) } } };
    }
}
