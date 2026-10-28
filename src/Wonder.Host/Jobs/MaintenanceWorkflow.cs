using Wonder.Host.Features.Chat;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Features.Emotion;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed class MaintenanceWorkflow(
    IDbContextFactory<WonderDbContext> factory,
    EmotionEngine engine,
    ConversationClock clock,
    SummaryWorkflow summaries,
    InteractionBoundary interaction)
{
    public async Task SweepSessionsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.Sessions.Where(x => x.Status == SessionStatus.Active && x.DeadlineAt <= now)
            .OrderBy(x => x.DeadlineAt).ThenBy(x => x.Id).Take(20).ToListAsync(ct);
        foreach (var session in sessions)
        {
            if (await db.Outbox.AnyAsync(
                    x => x.ConversationId == session.ConversationId &&
                         (x.Type == JobKind.Chat || x.Type == JobKind.Outgoing) &&
                         (x.Status == JobStatus.Pending || x.Status == JobStatus.Running), ct)) continue;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            session.Status = SessionStatus.Reflecting;
            session.Revision++;
            session.ClosingBoundary =
                await db.Messages.Where(x => x.SessionId == session.Id).MaxAsync(x => (long?)x.Sequence, ct) ?? 0;
            var subjects = await db.Messages
                .Where(x => x.SessionId == session.Id && x.SenderId != null && x.AllowReflection &&
                            !x.ContextInvalidated &&
                            db.Conversations.Any(c =>
                                c.Id == session.ConversationId && c.Kind == ConversationKind.Private))
                .Select(x => x.SenderId!.Value).Distinct().ToListAsync(ct);
            foreach (var subject in subjects)
            {
                var user = await db.LogicalUsers.AsNoTracking().SingleAsync(x => x.Id == subject, ct);
                if (user.AllowMemory && await UserFence.CheckAsync(db, user.Id, user.RevocationEpoch, ct))
                    await ReflectionRuns.AdmitAsync(db, session.BotId, session.ConversationId,
                        new ReflectionJob(session.Id, subject, session.ClosingBoundary.Value, user.RevocationEpoch,
                            ReflectionKind.Full), ct);
            }

            if (subjects.Count == 0) session.Status = SessionStatus.Closed;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var reflecting = await db.Sessions.Where(x => x.Status == SessionStatus.Reflecting)
            .OrderBy(x => x.DeadlineAt).ThenBy(x => x.Id).Take(30).ToListAsync(ct);
        foreach (var session in reflecting)
        {
            // Wait for generation and each independently committed reflection consumer.
            var refs = await db.Reflections.Where(x => x.SessionId == session.Id).Select(x => x.Id).ToListAsync(ct);
            var pending = await db.Outbox.Where(x =>
                x.ConversationId == session.ConversationId &&
                (x.Status == JobStatus.Pending || x.Status == JobStatus.Running) &&
                (x.Type == JobKind.Reflection || x.Type == JobKind.Emotion || x.Type == JobKind.Relationship ||
                 x.Type == JobKind.Memory)).Select(x => x.Payload).ToListAsync(ct);
            if (!pending.Any(p =>
                    p.Contains(session.Id.ToString(), StringComparison.Ordinal) ||
                    refs.Any(r => p.Contains(r.ToString(), StringComparison.Ordinal))))
            {
                session.Status = SessionStatus.Closed;
                session.Revision++;
            }
        }

        await db.SaveChangesAsync(ct);
        // Recover a crash after a permanent Chat failure committed but before its refund was recorded.
        var uncompensated = await db.ForceReceipts.Where(x => x.CompensationStatus == null &&
                                                              db.Outbox.Any(j =>
                                                                  j.Id == x.ChatJobId &&
                                                                  j.Status == JobStatus.DeadLetter))
            .OrderBy(x => x.AcceptedAt).ThenBy(x => x.Id).Select(x => x.ChatJobId).Take(50).ToArrayAsync(ct);
        foreach (var chat in uncompensated) await interaction.CompensateAsync(chat, ct);
    }

    public async Task ScheduleDailyAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(clock.Local(clock.UtcNow).DateTime);
        var bots = await db.Bots.Where(x => x.Enabled).Select(x => x.Id).ToListAsync(ct);
        foreach (var bot in bots)
        {
            // Catch up the latest missed day, not an unbounded backlog of stale AI generations.
            if (await db.Outbox.AnyAsync(
                    x => x.BotId == bot && x.Type == JobKind.Maintenance && x.ScheduledDate == today, ct)) continue;
            var job = Jobs.Add(db, bot, JobKind.Maintenance, new DailyJob(today));
            job.ScheduledDate = today;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        await db.ConversationReplays.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        var projections = await db.Summaries.AsNoTracking()
            .Where(x => x.ProjectionExpiresAt <= now && x.SafeProjectionJson != null)
            .OrderBy(x => x.ProjectionExpiresAt).ThenBy(x => x.Id).Take(100)
            .Select(x => new { x.Id, x.ProjectionRevision }).ToListAsync(ct);
        foreach (var projection in projections)
            await SummaryProjections.ExpireAsync(db, projection.Id, projection.ProjectionRevision, now, ct);
        var expiredIds = await db.Messages.Where(x => x.ExpiresAt <= now).OrderBy(x => x.ExpiresAt).ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(250).ToListAsync(ct);
        await ConversationReplay.PurgeSourcesAsync(db, "Message", expiredIds, ct);
        await db.Messages.Where(x => expiredIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var assets = await db.Media.Where(x => x.ExpiresAt <= now || !db.Messages.Any(m => m.Id == x.MessageId))
            .OrderBy(x => x.ExpiresAt).ThenBy(x => x.Id).Take(100)
            .ToListAsync(ct);
        foreach (var asset in assets)
        {
            await ConversationReplay.PurgeSourcesAsync(db, "Media", [asset.Id], ct);
            if (File.Exists(asset.Path)) File.Delete(asset.Path);
            db.Media.Remove(asset);
        }

        await db.BindingChallenges.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        var staleReservation = now.AddDays(-1);
        await db.Usage.Where(x => x.ReservationExpiresAt <= staleReservation).ExecuteDeleteAsync(ct);
        var cutoff = now.AddDays(-30);
        var done = await db.Batches.Where(x =>
                db.Outbox.Any(j => j.Id == x.SourceJobId && j.CreatedAt < cutoff) && !db.Parts.Any(p =>
                    p.BatchId == x.Id && (p.Status == DeliveryStatus.Pending || p.Status == DeliveryStatus.Sending ||
                                          p.Status == DeliveryStatus.DeliveryUnknown)))
            .OrderBy(x => x.Id).Select(x => x.Id).Take(100).ToListAsync(ct);
        await db.Parts.Where(x => done.Contains(x.BatchId)).ExecuteDeleteAsync(ct);
        await db.Batches.Where(x => done.Contains(x.Id)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RunAsync(Guid jobId, CancellationToken ct, Guid? expectedLeaseId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        Jobs.RequireLease(job, expectedLeaseId);
        if (job.Type == JobKind.Maintenance)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var mood = await db.Emotions.SingleAsync(x => x.BotId == job.BotId, ct);
            engine.Evolve(mood, DateTimeOffset.UtcNow);
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        if (job.Type == JobKind.Summary)
        {
            await summaries.RunAsync(jobId, ct, expectedLeaseId);
            return;
        }

        Jobs.Complete(db, job);
        await db.SaveChangesAsync(ct);
    }
}