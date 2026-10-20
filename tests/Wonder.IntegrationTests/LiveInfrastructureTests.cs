using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class LiveInfrastructureTests
{
    [Test]
    public async Task PostgreSqlMigrationsVectorAndRedisConsumerGroupWorkTogether()
    {
        var pg = Environment.GetEnvironmentVariable("WONDER_TEST_POSTGRES");
        var redis = Environment.GetEnvironmentVariable("WONDER_TEST_REDIS");
        if (string.IsNullOrEmpty(pg) || string.IsNullOrEmpty(redis))
        {
            Skip.Test("Set WONDER_TEST_POSTGRES and WONDER_TEST_REDIS for the disposable infrastructure test.");
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
        using var queue = await ConnectionMultiplexer.ConnectAsync(redis);
        var key = "wonder:test:" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new DbContextOptionsBuilder<WonderDbContext>().UseNpgsql(cs.ConnectionString).Options;
            await using var db = new WonderDbContext(options);
            await db.Database.MigrateAsync();
            await Assert.That(db.Database.HasPendingModelChanges()).IsFalse();
            var user = new ApplicationUser { Id = Guid.NewGuid() };
            db.Users.Add(user);
            var bot = new BotInstance { OwnerId = user.Id };
            db.Bots.Add(bot);
            var job = Jobs.Add(db, bot.Id, JobTypes.Maintenance, new IdPayload(bot.Id));
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
                    { MemoryId = memory.Id, ProfileId = profile.Id, Values = [1, 0, 0] });
            db.Embeddings.Add(new MemoryEmbedding
                { MemoryId = groupFact.Id, ProfileId = profile.Id, Values = [0, 1, 0] });
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
            db.Embeddings.Add(new MemoryEmbedding { MemoryId = sharedFact.Id, ProfileId = profile.Id, Values = [1, 0, 0] });
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
            await Assert.That((await db.Summaries.AsNoTracking().SingleAsync()).MessageIdsJson).IsEqualTo("[]");
            await Assert.That((await db.Models.AsNoTracking().SingleAsync()).SupportsTools).IsTrue();

            var stream = queue.GetDatabase();
            await stream.StreamCreateConsumerGroupAsync(key, "test", "0-0");
            await stream.StreamAddAsync(key, [new NameValueEntry("eventId", job.Id.ToString())]);
            var entries = await stream.StreamReadGroupAsync(key, "test", "consumer", ">", 1);
            await Assert.That(entries).HasSingleItem();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                Jobs.Complete(db, job);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            await stream.StreamAcknowledgeAsync(key, "test", entries[0].Id);
            await Assert.That((await stream.StreamPendingAsync(key, "test")).PendingMessageCount).IsEqualTo(0);
            await Assert.That(await db.Inbox.ToListAsync()).HasSingleItem();
            await Assert.That((await db.Outbox.AsNoTracking().SingleAsync()).Status).IsEqualTo(JobStatus.Completed);
        }
        finally
        {
            await queue.GetDatabase().KeyDeleteAsync(key);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}