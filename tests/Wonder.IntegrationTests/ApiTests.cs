using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using Wonder.Platforms;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.IntegrationTests;

public sealed class ApiTests
{
    [Test]
    [Arguments(null, "http://localhost", false)]
    [Arguments(null, "https://localhost", true)]
    [Arguments(true, "http://localhost", true)]
    [Arguments(true, "https://localhost", true)]
    public async Task ProductionLoginCookieFollowsTransportAndOptionalHttpsRequirement(
        bool? requireHttps, string baseAddress, bool secureCookie)
    {
        using var app = new TestApp(environment: "Production", requireHttps: requireHttps);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri(baseAddress) });
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var credentials = new { email = "transport@example.com", password = "A-useful-test-password!" };
        await Assert.That((await client.PostAsJsonAsync("/api/v1/auth/register", credentials)).StatusCode)
            .IsEqualTo(HttpStatusCode.Created);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", credentials);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var cookie = login.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("wonder.session=", StringComparison.Ordinal));
        await Assert.That(cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase)).IsEqualTo(secureCookie);
        await Assert.That(cookie.Contains("; httponly", StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)).IsTrue();
        if (!secureCookie || client.BaseAddress!.Scheme == "https")
            await Assert.That((await client.GetAsync("/api/v1/me")).StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<(HttpClient Client, Guid UserId)> LoginAsync(TestApp app, string email)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var credentials = new { email, password = "A-useful-test-password!" };
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", credentials);
        await Assert.That(register.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", credentials);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return (client, me.GetProperty("id").GetGuid());
    }

    [Test]
    public async Task OwnerCannotAccessAnotherOwnersBotOrConfiguration()
    {
        using var app = new TestApp();
        var (a, _) = await LoginAsync(app, "a@example.com");
        var (b, _) = await LoginAsync(app, "b@example.com");
        var created = await a.PostAsJsonAsync("/api/v1/bots", new { name = "Bot A", identity = "我是 Wonder" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var bot = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = bot.GetProperty("id").GetGuid();
        foreach (var suffix in new[]
                 {
                     "", "/identity", "/binding", "/model-routes", "/model-routes/tokenizers", "/emotion", "/jobs",
                     "/usage"
                 })
            await Assert.That((await b.GetAsync($"/api/v1/bots/{id}{suffix}")).StatusCode)
                .IsEqualTo(HttpStatusCode.NotFound);
        await Assert
            .That((await b.PutAsJsonAsync($"/api/v1/bots/{id}/identity", new { content = "hijack" })).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await a.GetAsync($"/api/v1/bots/{id}")).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await a.PostAsync("/api/v1/system/backups", null)).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ModelRoutesUseDiscoveredTokenizersAndRejectUnavailableChoices()
    {
        using var app = new TestApp();
        var (client, _) = await LoginAsync(app, "tokenizers@example.com");
        var created = await client.PostAsJsonAsync("/api/v1/bots", new { name = "Tokenizer Bot", identity = "Wonder" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var url = $"/api/v1/bots/{id}/model-routes";
        var available = (await client.GetFromJsonAsync<TokenizerResponse[]>(url + "/tokenizers"))!;
        await Assert.That(available.Length > 0).IsTrue();
        var selected = available[0].Id;
        var route = new ModelRouteInput(ModelTaskKind.Chat, "https://model.example/v1", selected, "test-key");
        await Assert.That((await client.PutAsJsonAsync(url, new[] { route }, Json.Options)).StatusCode)
            .IsEqualTo(HttpStatusCode.NoContent);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var profile = await db.Models.AsNoTracking().SingleAsync(x => x.BotId == id);
        var counter = scope.ServiceProvider.GetRequiredService<TokenCounter>();
        await Assert.That(profile.TokenizerId).IsEqualTo("auto");
        await Assert.That(counter.Resolve(profile)).IsEqualTo(selected);
        await Assert.That(counter.Text(profile, "Hello world") > 0).IsTrue();
        await Assert
            .That((await client.PutAsJsonAsync(url, new[] { route with { TokenizerId = selected } }, Json.Options))
                .StatusCode)
            .IsEqualTo(HttpStatusCode.NoContent);
        var invalid =
            await client.PutAsJsonAsync(url, new[] { route with { TokenizerId = "not-installed" } }, Json.Options);
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")
            .GetProperty("x[0].TokenizerId").GetArrayLength() > 0).IsTrue();
        await Assert.That((await db.Models.AsNoTracking().SingleAsync(x => x.BotId == id)).TokenizerId)
            .IsEqualTo(selected);
    }

    [Test]
    public async Task OwnerStatusDoesNotGrantPrivateMemoryAccess()
    {
        using var app = new TestApp();
        var (owner, ownerId) = await LoginAsync(app, "owner@example.com");
        var (person, personId) = await LoginAsync(app, "person@example.com");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var subject = new LogicalUser { AllowMemory = true };
        var ownerSubject = new LogicalUser();
        db.LogicalUsers.AddRange(subject, ownerSubject);
        (await db.Users.SingleAsync(x => x.Id == personId)).LogicalUserId = subject.Id;
        (await db.Users.SingleAsync(x => x.Id == ownerId)).LogicalUserId = ownerSubject.Id;
        var bot = new BotInstance { OwnerId = ownerId };
        db.Bots.Add(bot);
        var memory = new Memory
        {
            BotId = bot.Id, SubjectId = subject.Id, FactKey = "color", Text = "喜欢蓝色"
        };
        db.Memories.Add(memory);
        await db.SaveChangesAsync();
        await Assert.That((await owner.GetAsync($"/api/v1/me/memories/{memory.Id}")).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await owner.DeleteAsync($"/api/v1/me/memories/{memory.Id}")).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await person.GetAsync($"/api/v1/me/memories/{memory.Id}")).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await person.DeleteAsync($"/api/v1/me/memories/{memory.Id}")).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        db.ChangeTracker.Clear();
        await Assert.That((await db.LogicalUsers.SingleAsync(x => x.Id == subject.Id)).RevocationEpoch).IsEqualTo(0);
        await Assert.That((await db.Memories.SingleAsync(x => x.Id == memory.Id)).Text).IsEqualTo(memory.Text);
        await Assert.That(await db.Revocations.ToListAsync()).IsEmpty();
    }

    [Test]
    public async Task DuplicateOneBotEventPersistsOneMessageAndOneChatJob()
    {
        using var app = new TestApp();
        var (_, owner) = await LoginAsync(app, "gateway@example.com");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var bot = new BotInstance { OwnerId = owner };
        db.Bots.Add(bot);
        db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id });
        var binding = new PlatformBinding { BotId = bot.Id, PlatformAccountId = "123" };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();
        var input = new NormalizedEvent("123", "100", "456", "456", ConversationKind.Private,
            [new MessageSegment(SegmentKind.Text, "hello")], DateTimeOffset.UtcNow);
        await ingestor.IngestAsync(binding.Id, input, default);
        await ingestor.IngestAsync(binding.Id, input, default);
        await Assert.That(await db.Messages.ToListAsync()).HasSingleItem();
        await Assert.That(await db.Outbox.Where(x => x.Type == JobKind.Chat).ToListAsync()).HasSingleItem();
        await Assert.That(await db.Sessions.ToListAsync()).HasSingleItem();
    }

    [Test]
    public async Task MutatingCookieRequestsRequireSameOriginHeader()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        await Assert
            .That((await client.PostAsJsonAsync("/api/v1/auth/register",
                new { email = "csrf@example.com", password = "sufficient-password" })).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await client.GetAsync("/api/v1/bots")).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await client.GetAsync("/health/live")).StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task QueuedTurnsIncludeThePreviousReplyAndOutgoingPartsAreNotResent()
    {
        using var app = new TestApp();
        var (_, owner) = await LoginAsync(app, "chat@example.com");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var bot = new BotInstance { OwnerId = owner };
        db.Bots.Add(bot);
        db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id });
        db.Identities.Add(new PersonaIdentityRevision { BotId = bot.Id, Revision = 1, Content = "你叫 Wonder，简洁地回复。" });
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        db.Models.Add(new ModelProfile
        {
            BotId = bot.Id, TaskKind = TaskKind.Chat, Endpoint = "https://model.example/v1/", Model = "test",
            ProtectedApiKey = protector.Protect("test-key")
        });
        var binding = new PlatformBinding { BotId = bot.Id, PlatformAccountId = "123" };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();
        await ingestor.IngestAsync(binding.Id,
            new NormalizedEvent("123", "1", "456", "456", ConversationKind.Private,
                [new MessageSegment(SegmentKind.Text, "第一条")], DateTimeOffset.UtcNow), default);
        await ingestor.IngestAsync(binding.Id,
            new NormalizedEvent("123", "2", "456", "456", ConversationKind.Private,
                [new MessageSegment(SegmentKind.Text, "第二条")], DateTimeOffset.UtcNow), default);
        var jobs = await db.Outbox.Where(x => x.Type == JobKind.Chat).OrderBy(x => x.Sequence).ToListAsync();
        await scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(jobs[0].Id, default);
        var outgoing = await db.Outbox.SingleAsync(x => x.Type == JobKind.Outgoing);
        await scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(outgoing.Id, default);
        await scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(jobs[1].Id, default);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(2);
        using var secondRequest = JsonDocument.Parse(app.ModelRequests[1]);
        await Assert.That(secondRequest.RootElement.GetProperty("messages").EnumerateArray().Any(x =>
            x.GetProperty("role").GetString() == "assistant" &&
            x.GetProperty("content").GetString()!.EndsWith("这是上一轮的回复。", StringComparison.Ordinal))).IsTrue();
        await Assert.That(app.Sender.Sent).HasSingleItem();
        await Assert.That(await db.Messages.CountAsync(x => x.Role == MessageRole.Assistant)).IsEqualTo(2);
        await Assert.That((await db.Messages.FirstAsync(x => x.Role == MessageRole.Assistant)).ContextSnapshotJson)
            .IsNotNull();
        // Inbox completion means an already completed chat job cannot generate another reply.
        await scope.ServiceProvider.GetRequiredService<ChatWorkflow>().RunAsync(jobs[0].Id, default);
        await Assert.That(app.ModelRequests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task UnknownDeliveryRemainsVisibleAndIsNotBlindlyRetried()
    {
        using var app = new TestApp();
        var (_, owner) = await LoginAsync(app, "delivery@example.com");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var bot = new BotInstance { OwnerId = owner };
        db.Bots.Add(bot);
        var conv = new Conversation { BotId = bot.Id };
        db.Conversations.Add(conv);
        db.Bindings.Add(new PlatformBinding { BotId = bot.Id });
        var session = new ConversationSession { BotId = bot.Id, ConversationId = conv.Id };
        db.Sessions.Add(session);
        var batch = new OutgoingMessageBatch
            { BotId = bot.Id, ConversationId = conv.Id, SessionId = session.Id, IsSystemNotice = true };
        db.Batches.Add(batch);
        db.Parts.Add(new OutgoingMessagePart { BatchId = batch.Id, Text = "hello" });
        var job = Jobs.Add(db, bot.Id, JobKind.Outgoing, new IdPayload(batch.Id), conv.Id);
        await db.SaveChangesAsync();
        app.Sender.Unknown = true;
        await Assert.That(() => scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(job.Id, default))
            .ThrowsExactly<DeliveryUnknownException>();
        db.ChangeTracker.Clear();
        await Assert.That((await db.Parts.SingleAsync()).Status).IsEqualTo(DeliveryStatus.DeliveryUnknown);
        app.Sender.Unknown = false;
        await Assert.That(() => scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(job.Id, default))
            .ThrowsExactly<DeliveryUnknownException>();
        await Assert.That(app.Sender.Sent).IsEmpty();
    }

    [Test]
    public async Task OpenApiDocumentsTheProtectedManagementAndMemoryRoutes()
    {
        using var app = new TestApp();
        var (client, _) = await LoginAsync(app, "schema@example.com");
        var response = await client.GetAsync("/openapi/v1.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var schema = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(schema);
        await Assert.That(json.RootElement.GetProperty("paths").TryGetProperty("/api/v1/me/memories/{id}", out _))
            .IsTrue();
        await Assert.That(json.RootElement.GetProperty("paths").TryGetProperty("/api/v1/system/backups", out _))
            .IsFalse();
        await Assert
            .That(json.RootElement.GetProperty("paths").TryGetProperty("/api/v1/me/memory-release-status", out _))
            .IsFalse();
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        await Assert.That(schemas.GetProperty("ModelTaskKind").GetProperty("enum").EnumerateArray()
            .Select(x => x.GetString()!).ToArray()).IsEquivalentTo(new[]
            { "Chat", "LightReflection", "StrongReflection", "RollingSummary", "Embedding" });
        await Assert.That(schemas.GetProperty("PrivacyOperationView").GetProperty("properties")
            .TryGetProperty("recoveryVerified", out _)).IsFalse();
        var output = Environment.GetEnvironmentVariable("WONDER_EXPORT_OPENAPI");
        if (!string.IsNullOrEmpty(output)) await File.WriteAllTextAsync(output, schema);
    }

    [Test]
    public async Task CorrectionDoesNotCreatePrivacyRevocations()
    {
        using var app = new TestApp();
        var (_, owner) = await LoginAsync(app, "ledger@example.com");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var bot = new BotInstance { OwnerId = owner };
        var user = new LogicalUser { AllowMemory = true };
        db.Bots.Add(bot);
        db.LogicalUsers.Add(user);
        var memory = new Memory
        {
            BotId = bot.Id, SubjectId = user.Id, FactKey = "color", Text = "喜欢蓝色",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.Memories.Add(memory);
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<MemoryService>();
        await service.EditAsync(user.Id, memory.Id, "现在更喜欢绿色", 1, "correct", default);
        await Assert.That(await db.Revocations.CountAsync()).IsEqualTo(0);
        var remaining = await db.Memories.Where(x => x.Status != MemoryStatus.Revoked && x.ArchivedAt == null)
            .ToListAsync();
        await Assert.That(remaining).HasSingleItem();
        await Assert.That(remaining[0].Text).IsEqualTo("现在更喜欢绿色");
        await Assert.That(remaining[0].FactKey).IsEqualTo("color");
    }

    [Test]
    public async Task EveryBusinessEndpointIsAMvcControllerAction()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        await client.GetAsync("/health/live");
        var source = app.Services.GetRequiredService<EndpointDataSource>();
        var business = source.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("api/v1", StringComparison.Ordinal) == true).ToArray();
        await Assert.That(business.Length).IsGreaterThan(0);
        foreach (var endpoint in business)
            await Assert.That(endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()).IsNotNull();
    }

    [Test]
    public async Task MvcValidatesRequestsAndPreservesEnumAndSecretSerialization()
    {
        using var app = new TestApp();
        using var anonymous = app.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Wonder", "1");
        var invalid =
            await anonymous.PostAsJsonAsync("/api/v1/auth/register", new { email = "missing-password@example.com" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var (client, _) = await LoginAsync(app, "mvc@example.com");
        var response = await client.PostAsJsonAsync("/api/v1/bots", new { name = "MVC Bot", identity = "Wonder" });
        var bot = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = bot.GetProperty("id").GetGuid();
        var configured = await client.PutAsJsonAsync($"/api/v1/bots/{id}/model-routes",
            new[]
            {
                new
                {
                    taskKind = "Chat", endpoint = "https://example.com/v1/", model = "test-model",
                    apiKey = "private-test-key"
                }
            });
        await Assert.That(configured.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var routes = await client.GetFromJsonAsync<JsonElement>($"/api/v1/bots/{id}/model-routes");
        await Assert.That(routes[0].GetProperty("taskKind").GetString()).IsEqualTo("Chat");
        await Assert.That(routes[0].GetProperty("hasApiKey").GetBoolean()).IsTrue();
        await Assert.That(routes[0].TryGetProperty("protectedApiKey", out _)).IsFalse();
        await Assert.That(routes[0].TryGetProperty("apiKey", out _)).IsFalse();
        var group = await client.PutAsJsonAsync($"/api/v1/bots/{id}/groups/123/policy",
            new { respondToMention = false });
        await Assert.That(group.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var policy = await group.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(policy.GetProperty("respondToMention").GetBoolean()).IsFalse();
        await Assert.That(policy.GetProperty("commandPrefix").GetString()).IsEqualTo("/wonder");
    }

    [Test]
    public async Task FlashTasksShareOneModelWithIndependentThinkingAndKeepEmbeddingSeparate()
    {
        using var app = new TestApp();
        var (client, _) = await LoginAsync(app, "flash@example.com");
        var created = await client.PostAsJsonAsync("/api/v1/bots", new { name = "Flash Bot", identity = "Wonder" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var url = $"/api/v1/bots/{id}/model-routes";
        var embedding = await client.PutAsJsonAsync(url, new[]
        {
            new
            {
                taskKind = "Embedding", endpoint = "https://embedding.example/v1/", model = "vector-model",
                apiKey = "vector-key", embeddingDimensions = 3
            }
        });
        await Assert.That(embedding.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var rejected = await client.PutAsJsonAsync(url,
            new[]
            {
                new
                {
                    taskKind = "DailyNarrative", endpoint = "https://model.example/v1/", model = "retired",
                    apiKey = "unused"
                }
            });
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var tasks = Enum.GetValues<TaskKind>().Where(x => x != TaskKind.Embedding && x != TaskKind.DailyNarrative)
            .ToArray();
        var configured = await client.PutAsJsonAsync(url, tasks.Select(task => new
        {
            taskKind = task.ToString(), endpoint = "https://api.deepseek.com", model = "deepseek-flash",
            apiKey = "shared-key", contextWindow = 32768,
            maxOutputTokens = task == TaskKind.StrongReflection ? 8192 : 2048,
            thinkingEnabled = task == TaskKind.StrongReflection,
            reasoningEffort = task == TaskKind.StrongReflection ? "high" : null,
            supportsJson = task is TaskKind.LightReflection or TaskKind.StrongReflection
        }));
        await Assert.That(configured.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var routes = await client.GetFromJsonAsync<JsonElement>(url);
        await Assert.That(routes.GetArrayLength()).IsEqualTo(5);
        var vector = routes.EnumerateArray().Single(x => x.GetProperty("taskKind").GetString() == "Embedding");
        await Assert.That(vector.GetProperty("model").GetString()).IsEqualTo("vector-model");
        await Assert.That(vector.GetProperty("embeddingDimensions").GetInt32()).IsEqualTo(3);
        using var scope = app.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<ModelRouter>();
        foreach (var task in tasks)
        {
            var route = routes.EnumerateArray().Single(x => x.GetProperty("taskKind").GetString() == task.ToString());
            await Assert.That(route.GetProperty("thinkingEnabled").GetBoolean())
                .IsEqualTo(task == TaskKind.StrongReflection);
            var profile = await router.ProfileAsync(id, task, default);
            await router.GenerateAsync(profile,
                [new ChatMessage(ChatRole.User, "Return JSON.")],
                profile.SupportsJson, default);
            using var sent = JsonDocument.Parse(app.ModelRequests[^1]);
            await Assert.That(sent.RootElement.GetProperty("model").GetString()).IsEqualTo("deepseek-flash");
            await Assert.That(sent.RootElement.GetProperty("thinking").GetProperty("type").GetString())
                .IsEqualTo(task == TaskKind.StrongReflection ? "enabled" : "disabled");
            await Assert.That(sent.RootElement.TryGetProperty("reasoning_effort", out var effort))
                .IsEqualTo(task == TaskKind.StrongReflection);
            if (task == TaskKind.StrongReflection) await Assert.That(effort.GetString()).IsEqualTo("high");
        }

        // Clearing the options returns to provider defaults and preserves the existing secret.
        var reset = await client.PutAsJsonAsync(url, new[]
        {
            new
            {
                taskKind = "StrongReflection", endpoint = "https://api.deepseek.com", model = "deepseek-flash",
                apiKey = (string?)null, thinkingEnabled = (bool?)null, reasoningEffort = (string?)null
            }
        });
        await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var restored = await router.ProfileAsync(id, TaskKind.StrongReflection, default);
        await Assert.That(restored.ThinkingEnabled).IsNull();
        await Assert.That(restored.ReasoningEffort).IsNull();
        await Assert.That(scope.ServiceProvider.GetRequiredService<SecretProtector>()
                .Unprotect(restored.ProtectedApiKey))
            .IsEqualTo("shared-key");
    }

    [Test]
    [Arguments(false, "high", "Chat")]
    [Arguments(null, "high", "Chat")]
    [Arguments(true, "this-effort-name-exceeds-thirty-two-characters", "Chat")]
    [Arguments(true, "high", "Embedding")]
    [Arguments(false, null, "Embedding")]
    public async Task InvalidThinkingOptionsAreRejected(bool? enabled, string? effort, string task)
    {
        using var app = new TestApp();
        var (client, _) = await LoginAsync(app, "invalid-thinking@example.com");
        var created = await client.PostAsJsonAsync("/api/v1/bots", new { name = "Bot", identity = "Wonder" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var response = await client.PutAsJsonAsync($"/api/v1/bots/{id}/model-routes", new[]
        {
            new
            {
                taskKind = task, endpoint = "https://api.deepseek.com", model = "deepseek-flash",
                apiKey = "test-key", thinkingEnabled = enabled, reasoningEffort = effort
            }
        });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ToolCapabilityIsChatOnlyAndOmittedUpdatesPreserveIt()
    {
        using var app = new TestApp();
        var (client, _) = await LoginAsync(app, "tool-settings@example.com");
        var created = await client.PostAsJsonAsync("/api/v1/bots", new { name = "Tools", identity = "Wonder" });
        var bot = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var url = $"/api/v1/bots/{bot}/model-routes";
        await Assert.That((await client.PutAsJsonAsync(url, new[]
        {
            new
            {
                taskKind = "Chat", endpoint = "https://model.example/v1/",
                model = "test", apiKey = "test-key", supportsTools = true
            }
        })).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await client.PutAsJsonAsync(url, new[]
        {
            new
            {
                taskKind = "Chat", endpoint = "https://model.example/v1/",
                model = "updated", apiKey = ""
            }
        })).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var routes = await client.GetFromJsonAsync<JsonElement>(url);
        await Assert.That(routes[0].GetProperty("supportsTools").GetBoolean()).IsTrue();
        await Assert.That((await client.PutAsJsonAsync(url, new[]
        {
            new
            {
                taskKind = "Embedding", endpoint = "https://model.example/v1/",
                model = "vectors", apiKey = "test-key", supportsTools = true
            }
        })).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await client.PutAsJsonAsync(url, new[]
        {
            new
            {
                taskKind = "Chat", endpoint = "https://model.example/v1/",
                model = "updated", apiKey = "", supportsTools = false
            }
        })).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        routes = await client.GetFromJsonAsync<JsonElement>(url);
        await Assert.That(routes[0].GetProperty("supportsTools").GetBoolean()).IsFalse();
    }
}