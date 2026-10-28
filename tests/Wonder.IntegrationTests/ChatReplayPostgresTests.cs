using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wonder.Core;
using Wonder.Host.Features.Chat;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class ChatReplayPostgresTests
{
    [Test]
    public async Task ExistingBaselineUpgradesAndConcurrentOlderTurnCannotOverwriteNewerCheckpoint()
    {
        await using var pg = await TestPostgres.CreateAsync("20261026051916_InitialCreate");
        await using (var migrate = new WonderDbContext(new DbContextOptionsBuilder<WonderDbContext>()
                         .UseNpgsql(pg.ConnectionString, o => o.UseVector()).Options))
        {
            await migrate.Database.MigrateAsync();
            await migrate.Database.MigrateAsync();
            await Assert.That(migrate.Database.HasPendingModelChanges()).IsFalse();
            await Assert.That((await migrate.Database.GetAppliedMigrationsAsync()).Count()).IsEqualTo(2);
        }
        using var app = pg.CreateApp();
        var binding = await ChatReplayTests.SeedAsync(app);
        var first = await ChatReplayTests.QueueAsync(app, binding, "first");
        var next = await ChatReplayTests.QueueAsync(app, binding, "next");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.BeforeModelResponse = async body =>
        {
            var messages = ChatReplayTests.Messages(body);
            if (messages[^1].GetProperty("content").GetString()!.EndsWith("first", StringComparison.Ordinal))
            {
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };
        var older = ChatReplayTests.RunAsync(app, first.Job);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await ChatReplayTests.RunAsync(app, next.Job);
        }
        finally { release.TrySetResult(); }
        await older;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var state = await db.ConversationReplays.AsNoTracking().SingleAsync();
        await Assert.That(state.ThroughSequence).IsEqualTo(next.Message.Sequence);
        await Assert.That(state.Version).IsEqualTo(1);
        await Assert.That(await db.ReplayDependencies.AnyAsync()).IsTrue();
        await Assert.That(await db.Messages.CountAsync(x => x.Role == MessageRole.Assistant)).IsEqualTo(2);
        var user = await db.LogicalUsers.SingleAsync(x => x.Id == first.Message.SenderId);
        var read = await scope.ServiceProvider.GetRequiredService<ConversationReplay>().LoadAsync(db,
            first.Message.BotId, first.Message.ConversationId, user, first.Message.Sequence, "other", null,
            DateTimeOffset.UtcNow, default);
        await Assert.That(read.Replay).IsNull();
        await Assert.That(await db.ConversationReplays.CountAsync()).IsEqualTo(1);
        await Assert.That(await db.ConversationReplays.Where(x => x.ConversationId == state.ConversationId &&
                x.Version == 0).ExecuteUpdateAsync(s => s.SetProperty(x => x.PayloadJson, "stale"))).IsEqualTo(0);
        db.ConversationReplays.Add(new ConversationReplayState
        {
            ConversationId = state.ConversationId, BotId = state.BotId, UserId = state.UserId,
            UpdatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        var duplicate = await Assert.That(() => db.SaveChangesAsync()).ThrowsExactly<DbUpdateException>();
        await Assert.That(((PostgresException)duplicate!.InnerException!).SqlState).IsEqualTo("23505");
    }
}
