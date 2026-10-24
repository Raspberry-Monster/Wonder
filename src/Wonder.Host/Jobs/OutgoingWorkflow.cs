using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using Wonder.Platforms;

namespace Wonder.Host;

public sealed class OutgoingWorkflow(IDbContextFactory<WonderDbContext> factory, IPlatformConnection platform)
{
    public async Task RunAsync(Guid jobId, CancellationToken ct, Guid? expectedLeaseId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        Jobs.RequireLease(job, expectedLeaseId);
        if (job.Status == JobStatus.Completed) return;
        var batchId = Json.Read<IdPayload>(job.Payload).Id;
        var batch = await db.Batches.SingleAsync(x => x.Id == batchId, ct);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == batch.ConversationId, ct);
        var binding = await db.Bindings.AsNoTracking().SingleAsync(x => x.BotId == job.BotId && x.Enabled, ct);
        var parts = await db.Parts.Where(x => x.BatchId == batchId).OrderBy(x => x.Sequence).ToListAsync(ct);
        var generated = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == job.BotId &&
            x.ExternalMessageId == "generated:" + batch.SourceJobId, ct);
        var retiredContext = generated?.ContextInvalidated == true;
        if (generated is not null)
        {
            if (!ConversationHistory.TryMemoryIds(generated.ContextSnapshotJson, true, out var ids)) retiredContext = true;
            else retiredContext |= await MemoryQueries.RetiredGroupSources(db).AnyAsync(x => ids.Contains(x.Id), ct);
            if (conversation.Kind == ConversationKind.Group)
            {
                try
                {
                    var snapshot = Json.Read<ContextSnapshot>(generated.ContextSnapshotJson ?? "null");
                    var original = await db.Outbox.AsNoTracking().SingleOrDefaultAsync(x => x.Id == batch.SourceJobId, ct);
                    var sourceId = original is null ? Guid.Empty : Json.Read<IdPayload>(original.Payload).Id;
                    retiredContext |= snapshot is null || snapshot.RelationshipRevision != 0 || snapshot.SummaryRevision != 0 ||
                        snapshot.RetrievedMemoryIds.Count != 0 || snapshot.SourceMessageIds is null ||
                        snapshot.SourceMessageIds.Any(x => x != sourceId);
                }
                catch (System.Text.Json.JsonException) { retiredContext = true; }
            }
        }
        if (retiredContext)
        {
            foreach (var part in parts.Where(x => x.Status == DeliveryStatus.Pending)) part.Status = DeliveryStatus.Failed;
            if (generated is not null)
                await db.Messages.Where(x => x.Id == generated.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
            if (parts.Any(x => x.Status is DeliveryStatus.Sending or DeliveryStatus.DeliveryUnknown))
                throw new DeliveryUnknownException("Retired group context has an unconfirmed delivery; review required.");
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

            if (!await NoteSources.ValidAsync(db, "Chat", batch.SourceJobId, ct) ||
                await db.ChatDispositions.AnyAsync(x => x.ChatJobId == batch.SourceJobId, ct))
            {
                part.Status = DeliveryStatus.Failed;
                await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
                continue;
            }

            part.Status = DeliveryStatus.Sending;
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
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
                part.Status = DeliveryStatus.Pending;
                await Jobs.SaveAsync(db, job, expectedLeaseId, CancellationToken.None);
                throw;
            }
        }

        Jobs.Complete(db, job);
        await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
    }
}
