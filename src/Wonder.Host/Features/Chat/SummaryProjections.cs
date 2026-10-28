using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host;

public sealed class SummaryProjections(
    IDbContextFactory<WonderDbContext> factory, ModelRouter models, PromptCatalog prompts)
{
    public static OutboxMessage Queue(WonderDbContext db, ConversationSummary summary, Guid subject,
        Guid[] memoryIds, Guid[] messageIds, DateTimeOffset expiresAt)
    {
        return Jobs.Add(db, summary.BotId, JobKind.Summary,
            new SummaryJob(summary.SessionId, summary.ThroughSequence, new SummaryProjectionRequest(summary.Id, subject,
                summary.Revision, summary.ProjectionRevision, memoryIds, messageIds, expiresAt)));
    }

    // Rebuild from independently authorized current sources. The original unsafe Text is never sent to a model.
    // No projection is evidence for Memory extraction. Scheduling continues to use the existing Summary Outbox.
    public async Task<bool> RebuildAsync(Guid summaryId, Guid subject, long bodyRevision, long projectionRevision,
        IReadOnlyCollection<Guid> memoryIds, IReadOnlyCollection<Guid> messageIds, DateTimeOffset expiresAt,
        CancellationToken ct, Guid? jobId = null, Guid? expectedLeaseId = null)
    {
        if (expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddDays(7) ||
            memoryIds.Count + messageIds.Count is 0 or > 80)
            throw new ArgumentException("Invalid projection scope or TTL.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var target = await db.Summaries.AsNoTracking().SingleAsync(x => x.Id == summaryId, ct);
        if (target.Revision != bodyRevision || target.ProjectionRevision != projectionRevision) return false;
        OutboxMessage? job = null;
        if (jobId is { } id)
        {
            job = await db.Outbox.SingleAsync(x => x.Id == id && x.BotId == target.BotId && x.Type == JobKind.Summary,
                ct);
            Jobs.RequireLease(job, expectedLeaseId);
            if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return false;
        }

        var conversation = await db.Sessions.Where(x => x.Id == target.SessionId).Select(x => x.ConversationId)
            .SingleAsync(ct);
        if (!await db.Conversations.AnyAsync(x => x.Id == conversation && x.Kind == ConversationKind.Private, ct) ||
            !await db.Messages.AnyAsync(x => x.ConversationId == conversation && x.SenderId == subject, ct))
            throw new KeyNotFoundException();
        var facts = await db.Memories.AsNoTracking().Where(x => memoryIds.Contains(x.Id) &&
                                                                x.BotId == target.BotId && x.SubjectId == subject)
            .ToListAsync(ct);
        var messages = await db.Messages.AsNoTracking().Where(x => messageIds.Contains(x.Id) &&
                                                                   x.BotId == target.BotId &&
                                                                   x.ConversationId == conversation).ToListAsync(ct);
        if (facts.Count != memoryIds.Distinct().Count() || messages.Count != messageIds.Distinct().Count())
            return false;
        var refs = facts.Select(ContentSources.Memory).Concat(messages.Select(ContentSources.Message)).ToArray();
        if (!await ContentSources.ValidAsync(db, target.BotId, refs, ct, subject)) return false;
        var profile = await models.ProfileAsync(target.BotId, TaskKind.RollingSummary, ct);
        AiMessage[] request =
        [
            new(ChatRole.System,
                prompts.SummaryProjection),
            new(ChatRole.User, Json.ForPrompt(new
            {
                currentFacts = facts.Select(x => new { x.Text }),
                historicalStatements = messages.Select(x => new { x.Role, x.SenderId, x.CreatedAt, x.Text })
            }))
        ];
        models.Context(profile).Require(request);
        var text = (await models.GenerateAsync(profile, request, false, ct)).Text;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000)
            throw new ModelResponseException("Invalid safe projection.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var epoch = await db.LogicalUsers.Where(x => x.Id == subject).Select(x => x.RevocationEpoch).SingleAsync(ct);
        if (!await UserFence.CheckAsync(db, subject, epoch, ct) ||
            !await ContentSources.ValidAsync(db, target.BotId, refs, ct, subject)) return false;
        if (job is not null)
        {
            var instant = await InteractionBoundary.DatabaseNowAsync(db, DateTimeOffset.UtcNow, ct);
            if (expectedLeaseId is { } lease && await db.Outbox.Where(x => x.Id == job.Id &&
                                                                           x.Status == JobStatus.Running &&
                                                                           x.LeaseId == lease && x.LeaseUntil > instant)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, lease), ct) != 1)
                throw new DbUpdateConcurrencyException("Projection lease is no longer owned.");
            await db.Entry(job).ReloadAsync(ct);
            Jobs.RequireLease(job, expectedLeaseId);
            if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return false;
        }

        var updated = await db.Summaries.Where(x => x.Id == summaryId && x.Revision == bodyRevision &&
                                                    x.ProjectionRevision == projectionRevision).ExecuteUpdateAsync(s =>
            s
                .SetProperty(x => x.SafeProjectionJson, Json.Write(new SafeSummaryProjection(text, refs)))
                .SetProperty(x => x.ProjectionBaseSummaryRevision, bodyRevision)
                .SetProperty(x => x.ProjectionRevision, x => x.ProjectionRevision + 1)
                .SetProperty(x => x.ProjectionExpiresAt, expiresAt).SetProperty(x => x.ProjectionState, "Ready"), ct);
        if (job is not null)
        {
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
        }

        await tx.CommitAsync(ct);
        return updated == 1;
    }

    public static Task<int> ExpireAsync(WonderDbContext db, Guid id, long expectedProjectionRevision,
        DateTimeOffset now, CancellationToken ct)
    {
        return db.Summaries.Where(x => x.Id == id &&
                                       x.ProjectionRevision == expectedProjectionRevision &&
                                       x.ProjectionExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SafeProjectionJson, (string?)null)
                .SetProperty(x => x.ProjectionState, "Expired")
                .SetProperty(x => x.ProjectionRevision, x => x.ProjectionRevision + 1), ct);
    }
}
