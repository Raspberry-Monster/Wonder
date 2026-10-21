using Microsoft.EntityFrameworkCore;
using Wonder.Core;

namespace Wonder.Infrastructure;

// PostgreSQL is the durable queue as well as the source of task state.
public sealed class JobQueue(IDbContextFactory<WonderDbContext> factory)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    public async Task<OutboxMessage?> TryClaimAsync(string[] types, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        // Row locking makes claiming atomic across workers and hosts. An expired Running job is reclaimable.
        // Predecessors remain blockers even while delayed for a retry, preserving conversation ordering.
        var candidates = await db.Outbox.FromSqlInterpolated($"""
            SELECT j.* FROM "Outbox" j
            WHERE j."Type" = ANY ({types})
              AND j."Status" IN ({(int)JobStatus.Pending}, {(int)JobStatus.Running})
              AND j."NextAttemptAt" <= {now}
              AND (j."LeaseUntil" IS NULL OR j."LeaseUntil" <= {now})
              AND NOT EXISTS (
                  SELECT 1 FROM "Outbox" p
                  WHERE p."ConversationId" = j."ConversationId" AND p."Id" <> j."Id"
                    AND p."Status" IN ({(int)JobStatus.Pending}, {(int)JobStatus.Running})
                    AND ((j."Type" = {JobTypes.Chat} AND
                          ((p."Type" = {JobTypes.Chat} AND p."Sequence" < j."Sequence") OR p."Type" = {JobTypes.Outgoing}))
                      OR (j."Type" = {JobTypes.Outgoing} AND p."Type" = {JobTypes.Outgoing} AND p."Sequence" < j."Sequence")))
            ORDER BY j."Sequence"
            LIMIT 1 FOR UPDATE OF j SKIP LOCKED
            """).ToListAsync(ct);
        var job = candidates.SingleOrDefault();
        if (job is not null)
        {
            job.Status = JobStatus.Running;
            job.LeaseId = Guid.NewGuid();
            job.LeaseUntil = now.Add(LeaseDuration);
            job.Attempts++;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return job;
    }

    public async Task<bool> RenewAsync(Guid id, Guid leaseId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await db.Outbox.Where(x => x.Id == id && x.Status == JobStatus.Running &&
                x.LeaseId == leaseId && x.LeaseUntil > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, now.Add(LeaseDuration)), ct) == 1;
    }

    public async Task ReleaseAsync(Guid id, Guid leaseId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Outbox.Where(x => x.Id == id && x.Status == JobStatus.Running && x.LeaseId == leaseId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.Pending)
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseId, (Guid?)null), ct);
    }
}
