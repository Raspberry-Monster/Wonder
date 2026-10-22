using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

using Wonder.Core;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Infrastructure;

public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("Wonder.Secrets.v1");

    public string Protect(string secret)
    {
        return protector.Protect(secret);
    }

    public string Unprotect(string value)
    {
        return protector.Unprotect(value);
    }
}

public static class Jobs
{
    public static void RequireLease(OutboxMessage job, Guid? expectedLeaseId)
    {
        if (expectedLeaseId is { } lease && (job.Status != JobStatus.Running || job.LeaseId != lease))
            throw new DbUpdateConcurrencyException("The task lease is no longer owned by this worker.");
    }

    // Sending persists several intermediate states before completing the job. Fence each of those writes too.
    public static Task<int> SaveAsync(WonderDbContext db, OutboxMessage job, Guid? expectedLeaseId, CancellationToken ct)
    {
        if (expectedLeaseId is { } lease)
        {
            var token = db.Entry(job).Property(x => x.LeaseId);
            if (token.OriginalValue != lease)
                throw new DbUpdateConcurrencyException("The task lease changed during processing.");
            token.IsModified = true;
        }
        return db.SaveChangesAsync(ct);
    }

    public static OutboxMessage Add<T>(WonderDbContext db, Guid botId, string type, T payload,
        Guid? conversationId = null, Guid? cause = null, Guid? correlation = null)
    {
        if (!EventContracts.PayloadTypes.TryGetValue(type, out var payloadType) || payloadType != typeof(T))
            throw new ArgumentException("Event payload does not match its registered contract.", nameof(payload));
        var job = new OutboxMessage
        {
            BotId = botId,
            Type = type,
            Payload = Json.Write(payload),
            ConversationId = conversationId,
            CausationId = cause,
            CorrelationId = correlation ?? cause ?? Guid.NewGuid()
        };
        db.Outbox.Add(job);
        return job;
    }

    public static void Complete(WonderDbContext db, OutboxMessage job)
    {
        job.Status = JobStatus.Completed;
        job.LeaseUntil = null;
        job.LeaseId = null;
        job.ErrorCode = null;
        db.Inbox.Add(new InboxProcessedEvent { EventId = job.Id, HandlerName = job.Type });
    }
}

