using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using Wonder.Platforms;

namespace Wonder.Host;

public sealed class OutgoingWorkflow(
    IDbContextFactory<WonderDbContext> factory,
    IPlatformConnection platform,
    ConversationClock clock)
{
    public async Task RunAsync(Guid jobId, CancellationToken ct, Guid? expectedLeaseId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        Jobs.RequireLease(job, expectedLeaseId);
        if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return;
        var batchId = Json.Read<IdPayload>(job.Payload).Id;
        var batch = await db.Batches.SingleAsync(x => x.Id == batchId, ct);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == batch.ConversationId, ct);
        if (batch.BotId != job.BotId || conversation.BotId != job.BotId)
            throw new InvalidOperationException("Outgoing scope does not match its source bot.");
        var binding = await db.Bindings.AsNoTracking().SingleAsync(x => x.BotId == job.BotId && x.Enabled, ct);
        var parts = await db.Parts.Where(x => x.BatchId == batchId).OrderBy(x => x.Sequence).ToListAsync(ct);
        var generated = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == job.BotId &&
            x.ExternalMessageId == "generated:" + batch.SourceJobId, ct);
        var retiredContext = generated?.ContextInvalidated == true;
        if (generated is not null)
            if (!ConversationHistory.TryMemoryIds(generated.ContextSnapshotJson, true, out _))
                retiredContext = true;
        if (retiredContext)
        {
            foreach (var part in parts.Where(x => x.Status == DeliveryStatus.Pending))
            {
                part.Status = DeliveryStatus.Failed;
                part.FailureReason = "GenerationSourceInvalid";
            }

            if (generated is not null)
                await db.Messages.Where(x => x.Id == generated.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
            if (parts.Any(x => x.Status is DeliveryStatus.Sending or DeliveryStatus.DeliveryUnknown))
                throw new DeliveryUnknownException("Invalidated context has an unconfirmed delivery; review required.");
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            return;
        }

        // Protect already-queued replies from older versions as well, including markers split across parts.
        if (ToolCallMarkup.Contains(string.Concat(parts.Select(x => x.Text))))
        {
            foreach (var part in parts.Where(x => x.Status == DeliveryStatus.Pending))
                part.Status = DeliveryStatus.Failed;
            var externalId = "generated:" + batch.SourceJobId;
            await db.Messages.Where(x => x.BotId == job.BotId && x.ExternalMessageId == externalId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
            if (parts.Any(x => x.Status is DeliveryStatus.Sending or DeliveryStatus.DeliveryUnknown))
            {
                await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
                throw new DeliveryUnknownException("Unconfirmed delivery contains tool markup and requires review.");
            }

            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            return;
        }

        foreach (var part in parts)
        {
            await db.Entry(part).ReloadAsync(ct);
            if (part.Status == DeliveryStatus.Sent || part.Status == DeliveryStatus.Failed) continue;
            if (part.Status is DeliveryStatus.Sending or DeliveryStatus.DeliveryUnknown)
            {
                part.Status = DeliveryStatus.DeliveryUnknown;
                await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
                throw new DeliveryUnknownException("Unconfirmed OneBot delivery requires review.");
            }

            await using (var claim = await db.Database.BeginTransactionAsync(ct))
            {
                if (expectedLeaseId is { } lease)
                {
                    var instant = await InteractionBoundary.DatabaseNowAsync(db, clock.UtcNow, ct);
                    if (await db.Outbox.Where(x => x.Id == job.Id && x.Status == JobStatus.Running &&
                                                   x.LeaseId == lease && x.LeaseUntil > instant)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, lease), ct) != 1)
                        throw new DbUpdateConcurrencyException("Outgoing lease expired before claim.");
                }

                var proof = batch.SourceSnapshotJson ?? generated?.ContextSnapshotJson;
                ContextSnapshot? snapshot = null;
                try
                {
                    if (proof is not null) snapshot = Json.Read<ContextSnapshot>(proof);
                }
                catch (JsonException)
                {
                }

                var users = (snapshot?.SourceUserIds ?? []).ToHashSet();
                var roots = (snapshot?.Sources ?? []).ToList();
                if (generated is not null) roots.Add(ContentSources.Message(generated));
                var actualActors = await ContentSourceActors.ResolveAsync(db, job.BotId, roots, ct);
                if (actualActors is not null) users.UnionWith(actualActors);
                var memoryIds = snapshot?.RetrievedMemoryIds ?? [];
                var memoryUsers = await db.Memories.Where(x => memoryIds.Contains(x.Id) && x.BotId == job.BotId)
                    .Select(x => x.SubjectId).ToArrayAsync(ct);
                users.UnionWith(memoryUsers);
                var noteUsers = await (from d in db.NoteDependencies
                    join n in db.Notes on d.NoteId equals n.Id
                    where d.TargetKind == NoteDependencyTargetKind.Chat && d.TargetId == batch.SourceJobId
                    select n.UserId).ToArrayAsync(ct);
                users.UnionWith(noteUsers);
                foreach (var userId in users.Order())
                {
                    var epoch = await db.LogicalUsers.Where(x => x.Id == userId).Select(x => (long?)x.RevocationEpoch)
                        .SingleOrDefaultAsync(ct);
                    if (epoch is null || !await UserFence.CheckAsync(db, userId, epoch.Value, ct))
                        throw new DbUpdateConcurrencyException("Outgoing source policy changed.");
                }

                var fresh = generated is null
                    ? null
                    : await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == generated.Id, ct);
                var valid = (batch.IsSystemNotice && generated is null) ||
                            (fresh is null && snapshot?.Sources is { Count: > 0 } &&
                             snapshot.BotInstanceId == job.BotId &&
                             await ContentSources.ValidAsync(db, job.BotId, snapshot.Sources, ct, now: clock.UtcNow)) ||
                            (fresh is not null &&
                             !fresh.ContextInvalidated &&
                             await ContentSources.MessageValidAsync(db, fresh, ct, now: clock.UtcNow) &&
                             snapshot?.Sources is not null && snapshot.BotInstanceId == job.BotId &&
                             await ContentSources.ValidAsync(db, job.BotId, snapshot.Sources, ct, now: clock.UtcNow));
                valid = valid && actualActors is not null &&
                        await NoteSources.ValidAsync(db, NoteDependencyTargetKind.Chat, batch.SourceJobId, ct) &&
                        !await db.ChatDispositions.AnyAsync(x => x.ChatJobId == batch.SourceJobId, ct);
                if (!valid)
                {
                    await db.Parts.Where(x => x.BatchId == batch.Id && x.Status == DeliveryStatus.Pending)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(x => x.Status, DeliveryStatus.Failed)
                                .SetProperty(x => x.FailureReason, "GenerationSourceInvalid"), ct);
                    await claim.CommitAsync(ct);
                    continue;
                }

                if (await db.Parts.Where(x => x.Id == part.Id && x.Status == DeliveryStatus.Pending)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeliveryStatus.Sending), ct) != 1)
                    throw new DbUpdateConcurrencyException("Outgoing part was claimed concurrently.");
                await claim.CommitAsync(ct);
                await db.Entry(part).ReloadAsync(ct);
            }

            try
            {
                part.ExternalId = await platform.SendAsync(binding.Id, conversation.Kind, conversation.ExternalId,
                    part.Text, ct);
                part.Status = DeliveryStatus.Sent;
                await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            }
            catch (DeliveryUnknownException)
            {
                part.Status = DeliveryStatus.DeliveryUnknown;
                await Jobs.SaveAsync(db, job, expectedLeaseId, CancellationToken.None);
                throw;
            }
            catch (OperationCanceledException)
            {
                part.Status = DeliveryStatus.DeliveryUnknown;
                await Jobs.SaveAsync(db, job, expectedLeaseId, CancellationToken.None);
                throw;
            }
            catch
            {
                part.Status = DeliveryStatus.DeliveryUnknown;
                await Jobs.SaveAsync(db, job, expectedLeaseId, CancellationToken.None);
                throw new DeliveryUnknownException(
                    "Sending began but its result is unconfirmed; manual review required.");
            }
        }

        Jobs.Complete(db, job);
        await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
    }
}
