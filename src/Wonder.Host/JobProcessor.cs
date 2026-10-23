using Microsoft.EntityFrameworkCore;
using Wonder.Infrastructure.Models;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Platforms;

namespace Wonder.Host;

public sealed class JobProcessor(
    IDbContextFactory<WonderDbContext> factory,
    JobQueue queue,
    IServiceScopeFactory scopes,
    ILogger<JobProcessor> logger)
{
    public async Task ProcessAsync(OutboxMessage job, CancellationToken stopping)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var heartbeat = RenewAsync(job, execution, heartbeatStop.Token);
        try
        {
            EventContracts.ValidateVersion(job);
            if (job.Attempts > MaxAttempts(job.Type))
                throw new InvalidOperationException("Task exhausted its attempts after lease expiration.");
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var ct = execution.Token;
            switch (job.Type)
            {
                case JobTypes.Chat: await sp.GetRequiredService<ChatWorkflow>().RunAsync(job.Id, ct, job.LeaseId); break;
                case JobTypes.Outgoing: await sp.GetRequiredService<OutgoingWorkflow>().RunAsync(job.Id, ct, job.LeaseId); break;
                case JobTypes.Reflection: await sp.GetRequiredService<ReflectionWorkflow>().GenerateAsync(job.Id, ct, job.LeaseId); break;
                case JobTypes.Emotion or JobTypes.Relationship or JobTypes.Memory:
                    await sp.GetRequiredService<ReflectionWorkflow>().ApplyAsync(job.Id, ct, job.LeaseId); break;
                case JobTypes.Embedding: await sp.GetRequiredService<ReflectionWorkflow>().EmbedAsync(job.Id, ct, job.LeaseId); break;
                case JobTypes.MemoryMaintenance: await sp.GetRequiredService<MemoryMaintenanceWorkflow>().RunAsync(job.Id, ct, job.LeaseId); break;
                default: await sp.GetRequiredService<MaintenanceWorkflow>().RunAsync(job.Id, ct, job.LeaseId); break;
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!execution.IsCancellationRequested) await FailAsync(job, ex, stopping);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
            // Conditional release cannot disturb a completed task or a replacement lease.
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await queue.ReleaseAsync(job.Id, job.LeaseId!.Value, releaseTimeout.Token);
        }
    }

    private async Task RenewAsync(OutboxMessage job, CancellationTokenSource execution, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (!await queue.RenewAsync(job.Id, job.LeaseId!.Value, ct))
                {
                    await execution.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lease renewal failed for task {EventId}", job.Id);
            await execution.CancelAsync();
        }
    }

    private async Task FailAsync(OutboxMessage job, Exception ex, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var failed = await db.Outbox.SingleOrDefaultAsync(x => x.Id == job.Id && x.Status == JobStatus.Running &&
            x.LeaseId == job.LeaseId, ct);
        if (failed is null) return;
        var deferredQuota = ex is TokenQuotaException && job.Type != JobTypes.Chat;
        var terminal = !deferredQuota && (failed.Attempts >= MaxAttempts(job.Type) ||
            ex is ModelConfigurationException or DeliveryUnknownException or ContextLimitException or OutputLimitException or TokenQuotaException);
        failed.Status = terminal ? JobStatus.DeadLetter : JobStatus.Pending;
        failed.LeaseUntil = null;
        failed.LeaseId = null;
        failed.ErrorCode = ex.GetType().Name;
        failed.NextAttemptAt = DateTimeOffset.UtcNow.Add(ex is ProviderRetryException retry
            ? retry.RetryAfter : TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failed.Attempts) * 5)));
        if (deferredQuota)
        {
            failed.NextAttemptAt = ((TokenQuotaException)ex).RetryAt;
            failed.Attempts = Math.Max(0, failed.Attempts - 1);
        }
        if (terminal && job.Type == JobTypes.Chat && !await db.Batches.AnyAsync(x => x.SourceJobId == job.Id, ct))
        {
            var messageId = Json.Read<IdPayload>(job.Payload).Id;
            var session = await db.Messages.Where(x => x.Id == messageId).Select(x => x.SessionId).SingleOrDefaultAsync(ct);
            if (session is not null) ChatWorkflow.QueueReply(db, job, session.Value, ex switch
            {
                TokenQuotaException => "今天或本月的模型使用额度已用完，请在额度恢复后再试。",
                ContextLimitException => "这次消息或必要上下文超出了模型可处理的长度，请分段发送，或调整模型上下文与额度设置。",
                OutputLimitException => "这次生成达到了输出长度上限，请缩小问题范围或调整模型输出设置。",
                _ => "这次回复暂时没能完成，请稍后再试。"
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogWarning(ex, "Task {EventId} ({Type}) failed: {ErrorType}, terminal={Terminal}",
            job.Id, job.Type, ex.GetType().Name, terminal);
    }

    private static int MaxAttempts(string type) => type == JobTypes.Chat ? 2 : 5;
}
