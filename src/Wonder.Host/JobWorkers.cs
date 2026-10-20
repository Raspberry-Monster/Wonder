using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Wonder.AI;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Platforms;

namespace Wonder.Host;

public sealed class JobWorkers(
    IDbContextFactory<WonderDbContext> factory,
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopes,
    ILogger<JobWorkers> logger) : BackgroundService
{
    private const string Group = "wonder-v1";

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        return Task.WhenAll(
            PublishAsync(ct), ConsumeAsync("chat", "chat-0", ct), ConsumeAsync("chat", "chat-1", ct),
            ConsumeAsync("outgoing", "outgoing-0", ct), ConsumeAsync("reflection", "reflection-0", ct),
            ConsumeAsync("memory", "memory-0", ct), ConsumeAsync("maintenance", "maintenance-0", ct), SweepAsync(ct));
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var now = DateTimeOffset.UtcNow;
                var stale = now.AddMinutes(-1);
                var jobs = await db.Outbox.Where(x =>
                        x.Status != JobStatus.Completed && x.Status != JobStatus.DeadLetter && x.NextAttemptAt <= now &&
                        (x.LeaseUntil == null || x.LeaseUntil < now) &&
                        (x.PublishedAt == null || x.PublishedAt < stale))
                    .OrderBy(x => x.Sequence).Take(50).ToListAsync(ct);
                var queue = redis.GetDatabase();
                foreach (var job in jobs)
                {
                    await queue.StreamAddAsync(JobTypes.Stream(job.Type),
                    [
                        new NameValueEntry("eventId", job.Id.ToString()),
                        new NameValueEntry("schemaVersion", job.SchemaVersion),
                        new NameValueEntry("botId", job.BotId.ToString())
                    ]);
                    job.PublishedAt = now;
                }

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Outbox publish deferred: {ErrorType}", ex.GetType().Name);
            }

            await Delay(ct);
        }
    }

    private async Task ConsumeAsync(string suffix, string name, CancellationToken ct)
    {
        var stream = "bot:stream:" + suffix;
        var consumer = Environment.MachineName + "-" + name;
        var groupReady = false;
        while (!ct.IsCancellationRequested)
            try
            {
                var queue = redis.GetDatabase();
                if (!groupReady)
                {
                    // Initialize once. Repeated XGROUP CREATE raises BUSYGROUP even on a healthy Redis.
                    var exists = await queue.KeyExistsAsync(stream) &&
                                 (await queue.StreamGroupInfoAsync(stream)).Any(x => x.Name == Group);
                    if (!exists)
                        try
                        {
                            await queue.StreamCreateConsumerGroupAsync(stream, Group, "0-0", createStream: true);
                        }
                        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
                        {
                            // Another consumer initialized the same stream concurrently.
                        }
                    groupReady = true;
                }

                var recovered = await queue.StreamAutoClaimAsync(stream, Group, consumer, 600_000, "0-0", 5);
                var entries = recovered.ClaimedEntries.Length > 0
                    ? recovered.ClaimedEntries
                    : await queue.StreamReadGroupAsync(stream, Group, consumer, ">", 1);
                foreach (var entry in entries)
                {
                    var eventId = entry.Values.FirstOrDefault(x => x.Name == "eventId").Value;
                    if (Guid.TryParse(eventId.ToString(), out var id)) await ProcessAsync(id, ct);
                    await queue.StreamAcknowledgeAsync(stream, Group, entry.Id);
                    await queue.StreamDeleteAsync(stream, [entry.Id]);
                }

                if (entries.Length == 0) await Delay(ct);
            }
            catch (RedisServerException ex) when (!ct.IsCancellationRequested &&
                                                   ex.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
            {
                // Redis may have restarted without its stream data, or the group was removed.
                groupReady = false;
                logger.LogWarning(ex, "Stream {Stream} lost consumer group; recreating it", suffix);
                await Delay(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Stream {Stream} deferred: {ErrorType}", suffix, ex.GetType().Name);
                await Delay(ct);
            }
    }

    private async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        var now = DateTimeOffset.UtcNow;
        if (job is null || job.Status is JobStatus.Completed or JobStatus.DeadLetter || job.NextAttemptAt > now ||
            job.LeaseUntil > now) return;
        if (job.Type == JobTypes.Chat && await db.Outbox.AnyAsync(x =>
                x.ConversationId == job.ConversationId && x.Id != id &&
                (x.Status == JobStatus.Pending || x.Status == JobStatus.Running) &&
                ((x.Type == JobTypes.Chat && x.Sequence < job.Sequence) || x.Type == JobTypes.Outgoing), ct))
        {
            await DeferAsync(db, id, ct);
            return;
        }

        if (job.Type == JobTypes.Outgoing && await db.Outbox.AnyAsync(x =>
                x.ConversationId == job.ConversationId && x.Type == JobTypes.Outgoing && x.Sequence < job.Sequence &&
                (x.Status == JobStatus.Pending || x.Status == JobStatus.Running), ct))
        {
            await DeferAsync(db, id, ct);
            return;
        }

        if (await db.Outbox.Where(x =>
                    x.Id == id && (x.LeaseUntil == null || x.LeaseUntil < now) && x.Status != JobStatus.Completed &&
                    x.Status != JobStatus.DeadLetter)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.Status, JobStatus.Running)
                        .SetProperty(x => x.LeaseUntil, now.AddMinutes(10))
                        .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct) == 0) return;
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            switch (job.Type)
            {
                case JobTypes.Chat: await sp.GetRequiredService<ChatWorkflow>().RunAsync(id, ct); break;
                case JobTypes.Outgoing: await sp.GetRequiredService<OutgoingWorkflow>().RunAsync(id, ct); break;
                case JobTypes.Reflection:
                    await sp.GetRequiredService<ReflectionWorkflow>().GenerateAsync(id, ct); break;
                case JobTypes.Emotion or JobTypes.Relationship or JobTypes.Memory:
                    await sp.GetRequiredService<ReflectionWorkflow>().ApplyAsync(id, ct); break;
                case JobTypes.Embedding: await sp.GetRequiredService<ReflectionWorkflow>().EmbedAsync(id, ct); break;
                default: await sp.GetRequiredService<MaintenanceWorkflow>().RunAsync(id, ct); break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await using var failureDb = await factory.CreateDbContextAsync(ct);
            await using var tx = await failureDb.Database.BeginTransactionAsync(ct);
            var failed = await failureDb.Outbox.SingleAsync(x => x.Id == id, ct);
            if (failed.Status == JobStatus.Completed) return;
            var terminal = failed.Attempts >= (job.Type == JobTypes.Chat ? 2 : 5) ||
                           ex is ModelConfigurationException or DeliveryUnknownException;
            failed.Status = terminal ? JobStatus.DeadLetter : JobStatus.Pending;
            failed.LeaseUntil = null;
            failed.PublishedAt = null;
            failed.ErrorCode = ex.GetType().Name;
            failed.NextAttemptAt = DateTimeOffset.UtcNow.Add(ex is ProviderRetryException retry
                ? retry.RetryAfter
                : TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failed.Attempts) * 5)));
            if (terminal && job.Type == JobTypes.Chat &&
                !await failureDb.Batches.AnyAsync(x => x.SourceJobId == job.Id, ct))
            {
                var messageId = Json.Read<IdPayload>(job.Payload).Id;
                var session = await failureDb.Messages.Where(x => x.Id == messageId).Select(x => x.SessionId)
                    .SingleOrDefaultAsync(ct);
                if (session is not null) ChatWorkflow.QueueReply(failureDb, job, session.Value, "这次回复暂时没能完成，请稍后再试。");
            }

            await failureDb.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            logger.LogWarning(ex, "Job {EventId} ({Type}) failed: {ErrorType}, terminal={Terminal}", id, job.Type,
                ex.GetType().Name, terminal);
            if (terminal)
                await redis.GetDatabase().StreamAddAsync("bot:stream:dead-letter",
                [
                    new NameValueEntry("eventId", id.ToString()), new NameValueEntry("type", job.Type),
                    new NameValueEntry("error", ex.GetType().Name)
                ], maxLength: 1000, useApproximateMaxLength: true);
        }
    }

    private static Task<int> DeferAsync(WonderDbContext db, Guid id, CancellationToken ct)
    {
        return db.Outbox.Where(x => x.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.PublishedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(2)), ct);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().SweepAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Maintenance sweep deferred: {ErrorType}", ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    private static Task Delay(CancellationToken ct)
    {
        return Task.Delay(TimeSpan.FromSeconds(1), ct);
    }
}