using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class LiveInfrastructureTests
{
    [Test]
    public async Task PostgreSqlMigrationsVectorAndTaskQueueWorkTogether()
    {
        var pg = Environment.GetEnvironmentVariable("WONDER_TEST_POSTGRES");
        if (string.IsNullOrEmpty(pg))
        {
            Skip.Test("Set WONDER_TEST_POSTGRES for the disposable infrastructure test.");
            return;
        }

        var databaseName = "wonder_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(pg);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(pg) { Database = databaseName, Pooling = false };
        if (!cs.Database.StartsWith("wonder_test_", StringComparison.Ordinal) || cs.Database != databaseName)
            throw new InvalidOperationException("Migrations may only run against the newly created disposable test database.");
        try
        {
            var options = new DbContextOptionsBuilder<WonderDbContext>().UseNpgsql(cs.ConnectionString, pg => pg.UseVector()).Options;
            await using var db = new WonderDbContext(options);
            await VerifyUpgradeAsync(db, cs.ConnectionString);
            await db.Database.MigrateAsync();
            await Assert.That(db.Database.HasPendingModelChanges()).IsFalse();
            var user = new ApplicationUser { Id = Guid.NewGuid() };
            db.Users.Add(user);
            var bot = new BotInstance { OwnerId = user.Id };
            db.Bots.Add(bot);
            var job = Jobs.Add(db, bot.Id, JobTypes.Maintenance, new DailyJob(DateOnly.FromDateTime(DateTime.UtcNow)));
            await db.SaveChangesAsync();
            await Assert.That(job.Sequence > 0).IsTrue();
            var alice = new LogicalUser();
            var bob = new LogicalUser();
            db.LogicalUsers.AddRange(alice, bob);
            var profile = new EmbeddingProfile { BotId = bot.Id, Model = "test", Dimensions = 3 };
            db.EmbeddingProfiles.Add(profile);
            var privateFacts = Enumerable.Range(0, 6).Select(i => new Core.Memory
                { BotId = bot.Id, SubjectId = alice.Id, Scope = MemoryScope.Private, Text = "private-" + i }).ToArray();
            var groupFact = new Core.Memory
            {
                BotId = bot.Id, SubjectId = alice.Id, Scope = MemoryScope.GroupUser, ScopeOwnerId = "123",
                Text = "authorized group fact"
            };
            var otherUserFact = new Core.Memory
            {
                BotId = bot.Id, SubjectId = bob.Id, Scope = MemoryScope.GroupUser, ScopeOwnerId = "123",
                Text = "other user's fact"
            };
            db.Memories.AddRange(privateFacts);
            db.Memories.AddRange(groupFact, otherUserFact);
            foreach (var memory in privateFacts.Append(otherUserFact))
                db.Embeddings.Add(new MemoryEmbedding
                    { MemoryId = memory.Id, ProfileId = profile.Id, Values = new(new float[] { 1, 0, 0 }) });
            db.Embeddings.Add(new MemoryEmbedding
                { MemoryId = groupFact.Id, ProfileId = profile.Id, Values = new(new float[] { 0, 1, 0 }) });
            await db.SaveChangesAsync();
            var authorized = await MemoryQueries
                .Nearest(db, new MemoryAccess(bot.Id, alice.Id, "123"), profile.Id, [1, 0, 0]).ToListAsync();
            await Assert.That(authorized).HasSingleItem();
            await Assert.That(authorized[0].Id).IsEqualTo(groupFact.Id);

            bob.AllowMemory = true;
            bob.AllowCrossGroupMemory = true;
            var sharedSpace = new ContextSpace { BotId = bot.Id, Name = "project" };
            db.ContextSpaces.Add(sharedSpace);
            db.GroupPolicies.AddRange(new GroupPolicy { BotId = bot.Id, GroupId = "source", ContextSpaceId = sharedSpace.Id },
                new GroupPolicy { BotId = bot.Id, GroupId = "123", ContextSpaceId = sharedSpace.Id });
            var sourceFact = new Core.Memory { BotId = bot.Id, SubjectId = bob.Id, Scope = MemoryScope.GroupUser,
                ScopeOwnerId = "source", Text = "project source" };
            var sharedFact = new Core.Memory { BotId = bot.Id, SubjectId = bob.Id, Scope = MemoryScope.SpaceShared,
                ScopeOwnerId = sharedSpace.Id.ToString(), Text = "shared project", SharedFromMemoryId = sourceFact.Id };
            db.Memories.AddRange(sourceFact, sharedFact);
            db.Embeddings.Add(new MemoryEmbedding { MemoryId = sharedFact.Id, ProfileId = profile.Id, Values = new(new float[] { 1, 0, 0 }) });
            await db.SaveChangesAsync();
            var sharedAccess = new MemoryAccess(bot.Id, alice.Id, "123", sharedSpace.Id);
            var sharedMatches = await MemoryQueries.Nearest(db, sharedAccess, profile.Id, [1, 0, 0]).ToListAsync();
            await Assert.That(sharedMatches.Count).IsEqualTo(2);
            await Assert.That(sharedMatches[0].Id).IsEqualTo(sharedFact.Id);
            bob.AllowCrossGroupMemory = false;
            await db.SaveChangesAsync();
            var withdrawnMatches = await MemoryQueries.Nearest(db, sharedAccess, profile.Id, [1, 0, 0]).ToListAsync();
            await Assert.That(withdrawnMatches).HasSingleItem();
            await Assert.That(withdrawnMatches[0].Id).IsEqualTo(groupFact.Id);

            var conversation = new Conversation { BotId = bot.Id, Kind = ConversationKind.Group, ExternalId = "continuity" };
            var previousSession = new ConversationSession { BotId = bot.Id, ConversationId = conversation.Id, Status = SessionStatus.Closed };
            var currentSession = new ConversationSession { BotId = bot.Id, ConversationId = conversation.Id };
            db.Conversations.Add(conversation);
            db.Sessions.AddRange(previousSession, currentSession);
            var historical = new ChatMessage { BotId = bot.Id, ConversationId = conversation.Id, SessionId = previousSession.Id,
                SenderId = alice.Id, ExternalMessageId = "history", Text = "previous session", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
            db.Messages.Add(historical);
            await db.SaveChangesAsync();
            var summary = new ConversationSummary { BotId = bot.Id, SessionId = previousSession.Id,
                ThroughSequence = historical.Sequence, Text = "previous summary" };
            db.Summaries.Add(summary);
            var currentMessage = new ChatMessage { BotId = bot.Id, ConversationId = conversation.Id, SessionId = currentSession.Id,
                SenderId = alice.Id, ExternalMessageId = "current", Text = "continue", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
            db.Messages.Add(currentMessage);
            var toolProfile = new ModelProfile { BotId = bot.Id, TaskKind = TaskKind.Chat, SupportsTools = true };
            db.Models.Add(toolProfile);
            await db.SaveChangesAsync();
            await Assert.That((await ConversationHistory.LatestSummaryAsync(db, bot.Id, conversation.Id, currentMessage.Sequence, default))!.Id)
                .IsEqualTo(summary.Id);
            var retainedHistory = await ConversationHistory.Visible(db, bot.Id, conversation.Id, currentMessage.Sequence, DateTimeOffset.UtcNow)
                .OrderByDescending(x => x.Sequence).Take(5).ToListAsync();
            await Assert.That(retainedHistory).HasSingleItem();
            await Assert.That(retainedHistory[0].Id).IsEqualTo(historical.Id);
            await VerifyHistoryTimeFiltersAsync(options, historical.Id, currentMessage.Sequence);
            await Assert.That((await db.Summaries.AsNoTracking().SingleAsync()).MessageIdsJson).IsEqualTo("[]");
            await Assert.That((await db.Models.AsNoTracking().SingleAsync()).SupportsTools).IsTrue();

            // Exercise the complete autonomous workflow against real pgvector and the migrated schema.
            using (var app = new TestApp(postgresConnection: cs.ConnectionString))
            {
                var fixture = await MemoryMaintenanceTests.SeedAsync(app);
                app.ModelResponses.Enqueue(MemoryMaintenanceTests.Vector());
                app.ModelResponses.Enqueue(MemoryMaintenanceTests.Decision(MemoryMaintenanceAction.Update, fixture.Old, "我现在住上海。"));
                await MemoryMaintenanceTests.RunAsync(app, fixture.Job);
                var active = await db.Memories.AsNoTracking().SingleAsync(x => x.BotId == fixture.Bot && x.ArchivedAt == null);
                await Assert.That(active.SupersedesMemoryId).IsEqualTo(fixture.Old);
                await Assert.That((await db.Memories.AsNoTracking().SingleAsync(x => x.Id == fixture.Old)).ArchivedAt).IsNotNull();
                var matches = await MemoryQueries.Nearest(db, new(fixture.Bot, fixture.User, null),
                    (await db.EmbeddingProfiles.AsNoTracking().SingleAsync(x => x.BotId == fixture.Bot)).Id, [1, 0, 0]).ToListAsync();
                await Assert.That(matches).HasSingleItem();
                await Assert.That(matches[0].Id).IsEqualTo(active.Id);
                using var scope = app.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MemoryService>().RevokeAsync(fixture.User, active.Id, null, default);
                await Assert.That(await db.Memories.AnyAsync(x => x.BotId == fixture.Bot && x.Text != "")).IsFalse();
                await Assert.That(await db.MemoryCandidates.AnyAsync(x => x.BotId == fixture.Bot && x.Text != "")).IsFalse();
            }

            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                Jobs.Complete(db, job);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            await Assert.That(await db.Inbox.Where(x => x.EventId == job.Id).ToListAsync()).HasSingleItem();
            await Assert.That((await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == job.Id)).Status).IsEqualTo(JobStatus.Completed);
            await VerifyQueueAsync(options, bot.Id);
            await VerifyRetriesAsync(options, cs.ConnectionString, bot.Id);
            await VerifyBudgetAsync(options, bot.Id);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyHistoryTimeFiltersAsync(DbContextOptions<WonderDbContext> options, Guid messageId,
        long currentSequence)
    {
        await using var db = new WonderDbContext(options);
        // Read the persisted timestamp so comparisons use PostgreSQL's microsecond precision.
        var message = await db.Messages.AsNoTracking().SingleAsync(x => x.Id == messageId);
        var clock = new ConversationClock(TimeProvider.System, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var tools = new ConversationTools(new QueueFactory(options), clock);
        foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(8), TimeSpan.FromHours(-5) })
        {
            var start = message.CreatedAt.ToOffset(offset).ToString("O");
            var end = message.CreatedAt.AddSeconds(1).ToOffset(offset).ToString("O");
            var afterOnly = await Search(new() { ["after"] = start });
            await Assert.That(afterOnly).HasSingleItem();
            await Assert.That(afterOnly[0]).IsEqualTo(message.Id);
            var within = await Search(new() { ["after"] = start, ["before"] = end });
            await Assert.That(within).HasSingleItem();
            await Assert.That(within[0]).IsEqualTo(message.Id);
            await Assert.That(await Search(new() { ["before"] = start })).IsEmpty();
        }

        async Task<IReadOnlyList<Guid>> Search(Dictionary<string, object?> arguments)
        {
            var result = await tools.ExecuteAsync(new Microsoft.Extensions.AI.FunctionCallContent(
                "history-time-filter", "search_conversation_history", arguments), message.BotId,
                message.ConversationId, currentSequence, default);
            using var json = System.Text.Json.JsonDocument.Parse(result.Json);
            await Assert.That(json.RootElement.TryGetProperty("error", out _)).IsFalse();
            return result.MessageIds!;
        }
    }

    private static async Task VerifyBudgetAsync(DbContextOptions<WonderDbContext> options, Guid botId)
    {
        await using var db = new WonderDbContext(options);
        await db.Bots.Where(x => x.Id == botId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DailyTokenLimit, 1000));
        var budget = new TokenBudget(new QueueFactory(options));
        var profile = new ModelProfile { BotId = botId };
        async Task<Guid?> Reserve()
        {
            try { return await budget.ReserveAsync(profile, 700, default); }
            catch (Wonder.Infrastructure.Models.ModelConfigurationException) { return null; }
        }
        var results = await Task.WhenAll(Reserve(), Reserve());
        await Assert.That(results.Count(x => x.HasValue)).IsEqualTo(1);
        await budget.SettleAsync(results.Single(x => x.HasValue)!.Value, profile, 100, 100, default);
        var next = await budget.ReserveAsync(profile, 700, default);
        await budget.ReleaseAsync(next);
        await Assert.That(await db.Usage.Where(x => x.BotId == botId && x.ReservationExpiresAt == null)
            .SumAsync(x => x.InputTokens + x.OutputTokens)).IsEqualTo(200);
    }

    private static async Task VerifyUpgradeAsync(WonderDbContext db, string connection)
    {
        await db.GetService<IMigrator>().MigrateAsync("20261021093316_AutonomousMemoryMaintenance");
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), Email = "legacy@example.com", NormalizedEmail = "LEGACY@EXAMPLE.COM",
            UserName = "legacy@example.com", NormalizedUserName = "LEGACY@EXAMPLE.COM", LockoutEnabled = false
        };
        const string password = "Legacy-password-that-still-works!";
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, password);
        db.Users.Add(user);
        var bot = new BotInstance { OwnerId = user.Id };
        db.Bots.Add(bot);
        var profile = new EmbeddingProfile { BotId = bot.Id, Model = "legacy", Dimensions = 3 };
        var memory = new Core.Memory { BotId = bot.Id, SubjectId = Guid.NewGuid(), Text = "legacy vector" };
        db.EmbeddingProfiles.Add(profile);
        db.Memories.Add(memory);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Embeddings" ("MemoryId", "ProfileId", "Values") VALUES ({memory.Id}, {profile.Id}, ARRAY[1, 0, 0]::real[])
            """);
        var oldJob = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Outbox" ("Id", "BotId", "CorrelationId", "SchemaVersion", "Type", "Payload", "Status", "Attempts", "CreatedAt", "NextAttemptAt")
            VALUES ({oldJob}, {bot.Id}, {Guid.NewGuid()}, 1, 'maintenance', {Json.Write(new IdPayload(bot.Id))}, 0, 0, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
            """);
        await db.Database.MigrateAsync();
        var upgradedJob = await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == oldJob);
        await Assert.That(upgradedJob.SchemaVersion).IsEqualTo(2);
        await Assert.That(Json.Read<DailyJob>(upgradedJob.Payload).Date).IsEqualTo(DateOnly.FromDateTime(upgradedJob.CreatedAt.UtcDateTime));
        await Assert.That(upgradedJob.Status).IsEqualTo(JobStatus.Pending);
        await db.Entry(user).ReloadAsync();
        await Assert.That(user.LockoutEnabled).IsTrue();
        await Assert.That((await db.Embeddings.SingleAsync(x => x.MemoryId == memory.Id)).Values.ToArray())
            .IsEquivalentTo(new float[] { 1, 0, 0 });
        using var app = new TestApp(postgresConnection: connection);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = user.Email, password });
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task VerifyQueueAsync(DbContextOptions<WonderDbContext> options, Guid botId)
    {
        var queue = new JobQueue(new QueueFactory(options));
        await using var db = new WonderDbContext(options);
        var job = new OutboxMessage { BotId = botId, Type = "queue-probe", Payload = Json.Write(new IdPayload(botId)) };
        var delayed = new OutboxMessage { BotId = botId, Type = "queue-probe", Payload = Json.Write(new IdPayload(botId)) };
        db.Outbox.AddRange(job, delayed);
        delayed.NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => queue.TryClaimAsync(["queue-probe"], default)));
        await Assert.That(claims.OfType<OutboxMessage>().ToArray()).HasSingleItem();
        var first = claims.OfType<OutboxMessage>().Single();
        await Assert.That(first.Id).IsEqualTo(job.Id);
        await Assert.That(first.Attempts).IsEqualTo(1);
        await Assert.That(await queue.TryClaimAsync(["queue-probe"], default)).IsNull();
        await Assert.That(await queue.RenewAsync(first.Id, first.LeaseId!.Value, default)).IsTrue();
        await db.Entry(job).ReloadAsync();
        // Keep an old tracked copy, expire the lease, and verify both reclaim and stale-write fencing.
        await db.Outbox.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var replacement = (await queue.TryClaimAsync(["queue-probe"], default))!;
        await Assert.That(replacement.LeaseId == first.LeaseId).IsFalse();
        await Assert.That(replacement.Attempts).IsEqualTo(2);
        await Assert.That(await queue.RenewAsync(first.Id, first.LeaseId.Value, default)).IsFalse();
        await queue.ReleaseAsync(first.Id, first.LeaseId.Value, default);
        var bot = await db.Bots.SingleAsync(x => x.Id == botId);
        var originalName = bot.Name;
        bot.Name = "Stale worker must not persist this";
        await Assert.That(async () => { await Jobs.SaveAsync(db, job, first.LeaseId, default); })
            .ThrowsExactly<DbUpdateConcurrencyException>();
        job.Status = JobStatus.Completed;
        await Assert.That(async () => { await db.SaveChangesAsync(); }).ThrowsExactly<DbUpdateConcurrencyException>();
        db.ChangeTracker.Clear();
        await Assert.That((await db.Bots.SingleAsync(x => x.Id == botId)).Name).IsEqualTo(originalName);
        await Assert.That(() => Jobs.RequireLease(replacement, first.LeaseId)).ThrowsExactly<DbUpdateConcurrencyException>();
        await CompleteAsync(options, job.Id);
        await Assert.That(await queue.TryClaimAsync(["queue-probe"], default)).IsNull();

        var conversation = new Conversation { BotId = botId, ExternalId = "queue-order" };
        db.Conversations.Add(conversation);
        var earlier = Jobs.Add(db, botId, JobTypes.Chat, new IdPayload(botId), conversation.Id);
        var later = Jobs.Add(db, botId, JobTypes.Chat, new IdPayload(botId), conversation.Id);
        await db.SaveChangesAsync();
        // EF does not promise INSERT order; use the database sequence as the authoritative order.
        var ordered = new[] { earlier, later }.OrderBy(x => x.Sequence).ToArray();
        var chats = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => queue.TryClaimAsync([JobTypes.Chat], default)));
        await Assert.That(chats.OfType<OutboxMessage>().ToArray()).HasSingleItem();
        await Assert.That(chats.OfType<OutboxMessage>().Single().Id).IsEqualTo(ordered[0].Id);
        await CompleteAsync(options, ordered[0].Id);
        var outgoing = Jobs.Add(db, botId, JobTypes.Outgoing, new IdPayload(botId), conversation.Id);
        await db.SaveChangesAsync();
        await Assert.That(await queue.TryClaimAsync([JobTypes.Chat], default)).IsNull();
        await Assert.That((await queue.TryClaimAsync([JobTypes.Outgoing], default))!.Id).IsEqualTo(outgoing.Id);
        await CompleteAsync(options, outgoing.Id);
        await Assert.That((await queue.TryClaimAsync([JobTypes.Chat], default))!.Id).IsEqualTo(ordered[1].Id);
        await CompleteAsync(options, ordered[1].Id);
    }

    private static async Task CompleteAsync(DbContextOptions<WonderDbContext> options, Guid id)
    {
        await using var db = new WonderDbContext(options);
        Jobs.Complete(db, await db.Outbox.SingleAsync(x => x.Id == id));
        await db.SaveChangesAsync();
    }

    private static async Task VerifyRetriesAsync(DbContextOptions<WonderDbContext> options, string connection, Guid botId)
    {
        using var app = new TestApp(postgresConnection: connection);
        var queue = app.Services.GetRequiredService<JobQueue>();
        var processor = app.Services.GetRequiredService<JobProcessor>();
        await using var db = new WonderDbContext(options);
        // A missing summary session is a retryable failure; no model or platform request is made.
        var job = Jobs.Add(db, botId, JobTypes.Summary, new SummaryJob(Guid.NewGuid(), 1));
        await db.SaveChangesAsync();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await db.Outbox.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            var claim = (await queue.TryClaimAsync([JobTypes.Summary], default))!;
            await Assert.That(claim.Id).IsEqualTo(job.Id);
            await processor.ProcessAsync(claim, default);
            await db.Entry(job).ReloadAsync();
            await Assert.That(job.Attempts).IsEqualTo(attempt);
            await Assert.That(job.LeaseId).IsNull();
            await Assert.That(job.ErrorCode).IsEqualTo(nameof(InvalidOperationException));
            await Assert.That(job.Status).IsEqualTo(attempt == 5 ? JobStatus.DeadLetter : JobStatus.Pending);
            if (attempt < 5)
            {
                await Assert.That(job.NextAttemptAt > DateTimeOffset.UtcNow).IsTrue();
                await Assert.That(await queue.TryClaimAsync([JobTypes.Summary], default)).IsNull();
            }
        }
        await Assert.That(await queue.TryClaimAsync([JobTypes.Summary], default)).IsNull();
        // The existing owner retry API can return this durable dead-letter row to Pending.
        job.Status = JobStatus.Pending;
        job.Attempts = 0;
        job.ErrorCode = null;
        job.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        await Assert.That((await queue.TryClaimAsync([JobTypes.Summary], default))!.Attempts).IsEqualTo(1);
    }

    private sealed class QueueFactory(DbContextOptions<WonderDbContext> options) : IDbContextFactory<WonderDbContext>
    {
        public WonderDbContext CreateDbContext() => new(options);
    }
}
