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
        var now = DateTimeOffset.UtcNow;
        // The same eligibility predicate is used for selection and the atomic update.
        // No process lock or transaction is held while a job executes.
        var eligible = db.Outbox.Where(j => types.Contains(j.Type) &&
            (j.Status == JobStatus.Pending || j.Status == JobStatus.Running) && j.NextAttemptAt <= now &&
            (j.LeaseUntil == null || j.LeaseUntil <= now) &&
            !db.Outbox.Any(p => j.ConversationId != null && p.ConversationId == j.ConversationId && p.Id != j.Id &&
                (p.Status == JobStatus.Pending || p.Status == JobStatus.Running) &&
                ((j.Type == JobTypes.Chat && ((p.Type == JobTypes.Chat && p.Sequence < j.Sequence) || p.Type == JobTypes.Outgoing)) ||
                 (j.Type == JobTypes.Outgoing && p.Type == JobTypes.Outgoing && p.Sequence < j.Sequence))));
        var candidates = await eligible.AsNoTracking().OrderBy(x => x.Sequence).Take(8)
            .Select(x => new { x.Id, x.LeaseId }).ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            var lease = Guid.NewGuid();
            var updated = await eligible.Where(x => x.Id == candidate.Id && x.LeaseId == candidate.LeaseId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.Running)
                    .SetProperty(x => x.LeaseId, lease).SetProperty(x => x.LeaseUntil, now.Add(LeaseDuration))
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (updated == 1) return await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == candidate.Id, ct);
        }
        return null;
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
