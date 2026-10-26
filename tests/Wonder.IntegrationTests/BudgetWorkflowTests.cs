using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using StoredMessage = Wonder.Core.ChatMessage;

namespace Wonder.IntegrationTests;

public sealed class BudgetWorkflowTests
{
    [Test]
    public async Task SummaryResumesFromDatabaseAndNeverSkipsTheOldestBacklogOrLongFragments()
    {
        var folder = Directory.CreateTempSubdirectory("wonder-budget-");
        try
        {
            var path = Path.Combine(folder.FullName, "state.db");
            var fragments = new List<string>();
            Fixture f;
            Guid jobId;
            using (var app = new TestApp(databaseFile: path))
            {
                f = await SeedAsync(app, 130, true);
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
                var job = Jobs.Add(db, f.Bot, JobKind.Summary, new SummaryJob(f.Session, f.Messages[^1].Sequence),
                    f.Conversation);
                await db.SaveChangesAsync();
                jobId = job.Id;
                RecordSummaries(app, fragments);
                await scope.ServiceProvider.GetRequiredService<SummaryWorkflow>().RunAsync(jobId, default);
                await Assert.That((await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == jobId)).ProgressJson)
                    .IsNotNull();
                await Assert.That(await db.Summaries.AnyAsync()).IsFalse();
            }

            using (var restarted = new TestApp(databaseFile: path))
            {
                RecordSummaries(restarted, fragments);
                await DrainAsync(restarted, jobId, false);
                using var scope = restarted.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
                var summary = await db.Summaries.SingleAsync();
                await Assert.That(summary.ThroughSequence).IsEqualTo(f.Messages[^7].Sequence);
                await Assert.That(string.Concat(fragments))
                    .IsEqualTo(string.Concat(f.Messages.SkipLast(6).Select(x => x.Text)));
                var ids = Json.Read<Guid[]>(summary.MessageIdsJson);
                foreach (var message in f.Messages.SkipLast(6)) await Assert.That(ids).Contains(message.Id);
                var completed = await db.Outbox.SingleAsync(x => x.Id == jobId);
                await Assert.That(completed.ProgressJson).IsNull();
                await Assert.That(completed.Attempts).IsEqualTo(0);
            }
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Test]
    public async Task FullReflectionProcessesMoreThanOneHundredMessagesAndPublishesEmotionOnlyOnce()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app, 110, true);
        var seen = new HashSet<Guid>();
        app.BeforeModelResponse = body =>
        {
            using var request = JsonDocument.Parse(body);
            using var data =
                JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var ids = data.RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
            seen.UnionWith(ids);
            app.ModelResponses.Enqueue(Response(Json.Write(new ReflectionProposal(false,
                new EmotionProposal(.04, .02, .01, "", ids), [], []))));
            return Task.CompletedTask;
        };
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var job = Jobs.Add(db, f.Bot, JobKind.Reflection,
            new ReflectionJob(f.Session, f.User, f.Messages[^1].Sequence, 0, ReflectionKind.Full), f.Conversation);
        await db.SaveChangesAsync();
        await DrainAsync(app, job.Id, true);
        var result = await db.Reflections.AsNoTracking().SingleAsync();
        var proposal = Json.Read<ReflectionProposal>(result.Payload);
        await Assert.That(seen.Count).IsEqualTo(110);
        await Assert.That(proposal.Emotion!.Valence).IsEqualTo(.04).Within(.000001);
        await Assert.That(proposal.Emotion.EvidenceMessageIds.Length).IsEqualTo(110);
        await Assert.That(await db.Outbox.CountAsync(x => x.Type == JobKind.Emotion)).IsEqualTo(1);
        await Assert.That(await db.Outbox.CountAsync(x => x.Type == JobKind.Memory)).IsEqualTo(1);
    }

    [Test]
    public async Task RevokedSourceCancelsAnUnpublishedSummaryDraft()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app, 20, true);
        RecordSummaries(app, []);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var job = Jobs.Add(db, f.Bot, JobKind.Summary, new SummaryJob(f.Session, f.Messages[^1].Sequence),
            f.Conversation);
        await db.SaveChangesAsync();
        var workflow = scope.ServiceProvider.GetRequiredService<SummaryWorkflow>();
        await workflow.RunAsync(job.Id, default);
        await db.Messages.Where(x => x.Id == f.Messages[0].Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true));
        await workflow.RunAsync(job.Id, default);
        await Assert.That(await db.Summaries.AnyAsync()).IsFalse();
        var saved = await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        await Assert.That(saved.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(saved.ProgressJson).IsNull();
        await Assert.That(app.ModelRequests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EmbeddingRejectsOversizeBeforeSendingAndLostResponsesKeepEstimatedUsage()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app, 1);
        using var scope = app.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<ModelRouter>();
        var embedding = await router.ProfileAsync(f.Bot, TaskKind.Embedding, default);
        await Assert.That(async () => { await router.EmbedAsync(embedding, new string('大', 5000), default); })
            .ThrowsExactly<ContextLimitException>();
        await Assert.That(app.ModelRequests).IsEmpty();
        var profile = await router.ProfileAsync(f.Bot, TaskKind.RollingSummary, default);
        app.BeforeModelResponse = _ => throw new OperationCanceledException("response lost");
        await Assert.That(async () =>
        {
            await router.GenerateAsync(profile, [new AiMessage(ChatRole.User, "hello")], false, default);
        }).Throws<Exception>();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var usage = await db.Usage.SingleAsync();
        await Assert.That(usage.Estimated).IsTrue();
        await Assert.That(usage.ReservationExpiresAt).IsNull();
        await Assert.That(usage.OutputTokens).IsEqualTo(profile.MaxOutputTokens);
    }

    [Test]
    public async Task ExhaustedQuotaDefersBackgroundJobsWithoutUsingUpAttempts()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app, 1);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.Bots.Where(x => x.Id == f.Bot).ExecuteUpdateAsync(s => s.SetProperty(x => x.DailyTokenLimit, 10000));
        db.Usage.Add(new UsageRecord { BotId = f.Bot, InputTokens = 9950, OutputTokens = 0 });
        var job = Jobs.Add(db, f.Bot, JobKind.Reflection,
            new ReflectionJob(f.Session, f.User, f.Messages[^1].Sequence, 0, ReflectionKind.Light), f.Conversation);
        job.Status = JobStatus.Running;
        job.LeaseId = Guid.NewGuid();
        job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        job.Attempts = 1;
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(job, default);
        var saved = await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        await Assert.That(saved.Status).IsEqualTo(JobStatus.Pending);
        await Assert.That(saved.ErrorCode).IsEqualTo(nameof(TokenQuotaException));
        await Assert.That(saved.Attempts).IsEqualTo(0);
        await Assert.That(saved.NextAttemptAt).IsGreaterThan(DateTimeOffset.UtcNow);
        await Assert.That(app.ModelRequests).IsEmpty();
    }


    [Test]
    public async Task RetiredNarrativeJobsAreCancelledWithoutUsingTheModel()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app, 1);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.Bots.Where(x => x.Id == f.Bot).ExecuteUpdateAsync(s => s.SetProperty(x => x.DailyTokenLimit, 1000));
        db.Usage.Add(new UsageRecord { BotId = f.Bot, InputTokens = 950, OutputTokens = 0 });
        var job = new OutboxMessage
            { BotId = f.Bot, Type = JobKind.Narrative, Payload = "{}", ProgressJson = "old narrative draft" };
        db.Outbox.Add(job);
        job.Status = JobStatus.Running;
        job.LeaseId = Guid.NewGuid();
        job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        job.Attempts = 1;
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(job, default);
        var saved = await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        await Assert.That(saved.Status).IsEqualTo(JobStatus.Cancelled);
        await Assert.That(saved.ErrorCode).IsEqualTo("FeatureRetired");
        await Assert.That(saved.ProgressJson).IsNull();
        await Assert.That(app.ModelRequests).IsEmpty();
    }

    [Test]
    public async Task SmallerStrongReflectionRouteRepartitionsTheLightEvidence()
    {
        using var app = new TestApp();
        var f = await SeedAsync(app, 18, true);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.Models.Where(x => x.BotId == f.Bot && x.TaskKind == TaskKind.LightReflection)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextWindow, 8192));
        var strongFragments = new List<string>();
        var lightCalls = 0;
        app.BeforeModelResponse = body =>
        {
            using var request = JsonDocument.Parse(body);
            var strong = request.RootElement.GetProperty("model").GetString() == nameof(TaskKind.StrongReflection);
            using var content =
                JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            if (strong)
                strongFragments.AddRange(content.RootElement.EnumerateArray()
                    .Select(x => x.GetProperty("text").GetString()!));
            else lightCalls++;
            app.ModelResponses.Enqueue(Response(Json.Write(new ReflectionProposal(!strong, null, [], []))));
            return Task.CompletedTask;
        };
        var job = Jobs.Add(db, f.Bot, JobKind.Reflection,
            new ReflectionJob(f.Session, f.User, f.Messages[^1].Sequence, 0, ReflectionKind.Full), f.Conversation);
        await db.SaveChangesAsync();
        await DrainAsync(app, job.Id, true);
        await Assert.That(string.Concat(strongFragments)).IsEqualTo(string.Concat(f.Messages.Select(x => x.Text)));
        await Assert.That(strongFragments.Count).IsGreaterThan(lightCalls);
        await Assert.That(await db.Reflections.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task MemoryMaintenanceGroupsRelatedFactsAndCommitsOnlyAfterAllGroups()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var profile = await db.EmbeddingProfiles.SingleAsync();
        var original = await db.Memories.SingleAsync();
        original.Text = new string('a', 1400);
        for (var i = 0; i < 2; i++)
        {
            var memory = new Memory
            {
                BotId = f.Bot, SubjectId = f.User,
                Text = new string((char)('b' + i), 1400), FactKey = "related-" + i, Status = MemoryStatus.Active
            };
            db.Memories.Add(memory);
            db.Embeddings.Add(new MemoryEmbedding
                { MemoryId = memory.Id, ProfileId = profile.Id, Values = new Vector(new float[] { 1, 0, 0 }) });
        }

        (await db.Models.SingleAsync(x => x.TaskKind == TaskKind.StrongReflection)).ContextWindow = 6144;
        await db.SaveChangesAsync();
        var reviewed = 0;
        app.BeforeModelResponse = body =>
        {
            using var request = JsonDocument.Parse(body);
            if (request.RootElement.TryGetProperty("input", out _))
            {
                app.ModelResponses.Enqueue(MemoryMaintenanceTests.Vector());
            }
            else
            {
                using var content = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1]
                    .GetProperty("content").GetString()!);
                if (content.RootElement.GetProperty("related").GetArrayLength() != 1)
                    throw new InvalidOperationException("Expected a complete single-fact evidence group.");
                reviewed++;
                app.ModelResponses.Enqueue(MemoryMaintenanceTests.Decision(MemoryMaintenanceAction.Add, null));
            }

            return Task.CompletedTask;
        };
        await MemoryMaintenanceTests.RunAsync(app, f.Job);
        await Assert.That((await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == f.Job)).ProgressJson).IsNotNull();
        await Assert.That(await db.Memories.CountAsync()).IsEqualTo(3);
        for (var i = 0;
             i < 3 && (await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == f.Job)).Status != JobStatus.Completed;
             i++)
            await MemoryMaintenanceTests.RunAsync(app, f.Job);
        await Assert.That(reviewed).IsEqualTo(3);
        await Assert.That(await db.Memories.CountAsync()).IsEqualTo(4);
        await Assert.That((await db.MemoryCandidates.AsNoTracking().SingleAsync()).Status)
            .IsEqualTo(MemoryMaintenanceStatus.Applied);
    }

    [Test]
    public async Task IncompleteOrOversizeEvidenceDefersMaintenanceWithoutArchivingFacts()
    {
        await using var database = await TestPostgres.CreateAsync();
        using var app = database.CreateApp();
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.Messages.Where(x => x.Id == f.Evidence)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, new string('长', 10000)));
        app.ModelResponses.Enqueue(MemoryMaintenanceTests.Vector());
        await MemoryMaintenanceTests.RunAsync(app, f.Job);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(1);
        await Assert.That((await db.MemoryCandidates.AsNoTracking().SingleAsync()).Status)
            .IsEqualTo(MemoryMaintenanceStatus.Deferred);
        await Assert.That((await db.Memories.AsNoTracking().SingleAsync()).ArchivedAt).IsNull();
    }

    private static void RecordSummaries(TestApp app, List<string> fragments)
    {
        app.BeforeModelResponse = body =>
        {
            using var request = JsonDocument.Parse(body);
            using var data =
                JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            fragments.AddRange(data.RootElement.GetProperty("messages").EnumerateArray()
                .Select(x => x.GetProperty("text").GetString()!));
            app.ModelResponses.Enqueue(Response("已经概括的对话，保留原始作者与日期。"));
            return Task.CompletedTask;
        };
    }

    private static async Task DrainAsync(TestApp app, Guid job, bool reflection)
    {
        for (var i = 0; i < 100; i++)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            if ((await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == job)).Status == JobStatus.Completed) return;
            if (reflection)
                await scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().GenerateAsync(job, default);
            else await scope.ServiceProvider.GetRequiredService<SummaryWorkflow>().RunAsync(job, default);
        }

        throw new InvalidOperationException("Batch processing did not make progress.");
    }

    private static async Task<Fixture> SeedAsync(TestApp app, int count, bool longFirst = false)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        var user = new LogicalUser { AllowMemory = true };
        var conversation = new Conversation { BotId = bot.Id };
        var session = new ConversationSession { BotId = bot.Id, ConversationId = conversation.Id };
        db.Users.Add(owner);
        db.Bots.Add(bot);
        db.LogicalUsers.Add(user);
        db.Conversations.Add(conversation);
        db.Sessions.Add(session);
        db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id });
        foreach (var kind in new[]
                 {
                     TaskKind.RollingSummary, TaskKind.LightReflection, TaskKind.StrongReflection, TaskKind.Embedding
                 })
            db.Models.Add(new ModelProfile
            {
                BotId = bot.Id, TaskKind = kind, Endpoint = "https://model.example/v1/", Model = kind.ToString(),
                ContextWindow = 4096, MaxOutputTokens = 256, TokenizerId = "estimate", SupportsJson = true,
                ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key")
            });
        var rows = Enumerable.Range(0, count).Select(i => new StoredMessage
        {
            BotId = bot.Id, SenderId = user.Id,
            ConversationId = conversation.Id, SessionId = session.Id, ExternalMessageId = i.ToString(),
            AllowReflection = true,
            Text = $"开始 {i:D4}: " +
                   (i == 0 && longFirst ? string.Concat(Enumerable.Repeat("中文😀\n", 1500)) : "有作者和日期的对话。") + " 结束。",
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(90)
        }).ToArray();
        db.Messages.AddRange(rows);
        await db.SaveChangesAsync();
        return new Fixture(bot.Id, user.Id, session.Id, conversation.Id, rows);
    }

    private static string Response(string content)
    {
        return Json.Write(new
        {
            choices = new[] { new { message = new { content }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 20, completion_tokens = 30 }
        });
    }

    private sealed record Fixture(Guid Bot, Guid User, Guid Session, Guid Conversation, StoredMessage[] Messages);
}