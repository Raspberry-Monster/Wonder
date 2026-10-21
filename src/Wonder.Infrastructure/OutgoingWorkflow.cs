using Microsoft.EntityFrameworkCore;
using Wonder.AI;
using Wonder.Core;
using Wonder.Platforms;

namespace Wonder.Infrastructure;

public sealed class OutgoingWorkflow(IDbContextFactory<WonderDbContext> factory, IPlatformConnection platform)
{
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        if (job.Status == JobStatus.Completed) return;
        var batchId = Json.Read<IdPayload>(job.Payload).Id;
        var batch = await db.Batches.SingleAsync(x => x.Id == batchId, ct);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == batch.ConversationId, ct);
        var binding = await db.Bindings.AsNoTracking().SingleAsync(x => x.BotId == job.BotId && x.Enabled, ct);
        var parts = await db.Parts.Where(x => x.BatchId == batchId).OrderBy(x => x.Sequence).ToListAsync(ct);
        // Protect already-queued replies from older versions as well, including markers split across parts.
        if (ToolCallMarkup.Contains(string.Concat(parts.Select(x => x.Text))))
        {
            foreach (var part in parts.Where(x => x.Status == DeliveryStatus.Pending)) part.Status = DeliveryStatus.Failed;
            var externalId = "generated:" + batch.SourceJobId;
            await db.Messages.Where(x => x.BotId == job.BotId && x.ExternalMessageId == externalId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
            if (parts.Any(x => x.Status is DeliveryStatus.Sending or DeliveryStatus.DeliveryUnknown))
            {
                await db.SaveChangesAsync(ct);
                throw new DeliveryUnknownException("Unconfirmed delivery contains tool markup and requires review.");
            }
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            return;
        }
        foreach (var part in parts)
        {
            await db.Entry(part).ReloadAsync(ct);
            if (part.Status == DeliveryStatus.Sent || part.Status == DeliveryStatus.Failed) continue;
            if (part.Status is DeliveryStatus.Sending or DeliveryStatus.DeliveryUnknown)
            {
                part.Status = DeliveryStatus.DeliveryUnknown;
                await db.SaveChangesAsync(ct);
                throw new DeliveryUnknownException("Unconfirmed OneBot delivery requires review.");
            }

            part.Status = DeliveryStatus.Sending;
            await db.SaveChangesAsync(ct);
            try
            {
                part.ExternalId = await platform.SendAsync(binding.Id, conversation.Kind, conversation.ExternalId,
                    part.Text, ct);
                part.Status = DeliveryStatus.Sent;
                await db.SaveChangesAsync(ct);
            }
            catch (DeliveryUnknownException)
            {
                part.Status = DeliveryStatus.DeliveryUnknown;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (OperationCanceledException)
            {
                part.Status = DeliveryStatus.DeliveryUnknown;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch
            {
                part.Status = DeliveryStatus.Pending;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }

        Jobs.Complete(db, job);
        await db.SaveChangesAsync(ct);
    }
}
