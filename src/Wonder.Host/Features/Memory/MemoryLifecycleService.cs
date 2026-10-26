using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed partial class MemoryService
{
    public async Task RetryIndexAsync(Guid actor, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await db.LogicalUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor, ct)
                   ?? throw new KeyNotFoundException();
        if (!await UserFence.CheckAsync(db, actor, user.RevocationEpoch, ct)) throw new DbUpdateConcurrencyException();
        var memory = await db.Memories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.SubjectId == actor &&
            x.Status != MemoryStatus.Revoked && x.ArchivedAt == null && !x.IsOutdated, ct) ?? throw new KeyNotFoundException();
        var payload = Json.Write(new IdPayload(id));
        if (!await db.Outbox.AnyAsync(x => x.BotId == memory.BotId && x.Type == JobKind.Embedding && x.Payload == payload &&
                (x.Status == JobStatus.Pending || x.Status == JobStatus.Running), ct))
            Jobs.Add(db, memory.BotId, JobKind.Embedding, new IdPayload(id));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public Task<MemoryOperationReceipt> EditAsync(Guid actor, Guid id, string text, long expectedRevision,
        string key, CancellationToken ct) => ApplyLifecycleAsync(actor, id, text.Trim(), expectedRevision, key, ct);

    public Task<MemoryOperationReceipt> ArchiveAsync(Guid actor, Guid id, long expectedRevision,
        string key, CancellationToken ct) => ApplyLifecycleAsync(actor, id, null, expectedRevision, key, ct);

    private async Task<MemoryOperationReceipt> ApplyLifecycleAsync(Guid actor, Guid id, string? text,
        long expectedRevision, string key, CancellationToken ct)
    {
        if (expectedRevision < 1 || string.IsNullOrWhiteSpace(key) || key.Length > 128 ||
            text is { Length: 0 or > 1000 }) throw new ArgumentException("Revision, idempotency key and body are required.");
        var kind = text is null ? "Archive" : "Edit";
        var hash = Json.Hash(Json.Write(new { kind, id, text, expectedRevision }));
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await db.LogicalUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor, ct)
                   ?? throw new KeyNotFoundException();
        // Same lock order as maintenance and final source validation. No epoch bump for an unrelated fact.
        if (!await UserFence.CheckAsync(db, actor, user.RevocationEpoch, ct))
            throw new DbUpdateConcurrencyException("Policy changed; reload.");
        var prior = await db.MemoryOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActorId == actor && x.IdempotencyKey == key, ct);
        if (prior is not null)
        {
            if (prior.ArgumentsHash != hash) throw new DbUpdateConcurrencyException("Idempotency key was used for different arguments.");
            return Receipt(prior);
        }
        var memory = await db.Memories.SingleOrDefaultAsync(x => x.Id == id && x.SubjectId == actor &&
            x.Status != MemoryStatus.Revoked, ct) ?? throw new KeyNotFoundException();
        if (memory.Revision != expectedRevision || memory.ArchivedAt is not null || memory.IsOutdated)
            throw new DbUpdateConcurrencyException("The current memory changed; reload.");
        await ValidateFamilyAsync(db, memory, ct);
        if (text is not null)
        {
            var profile = await db.Models.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BotId == memory.BotId && x.TaskKind == TaskKind.Embedding, ct);
            if (profile is not null && models.Context(profile).Text(text) > models.Context(profile).InputLimit)
                throw new ArgumentException("The replacement exceeds the embedding input limit.");
        }
        memory.ArchivedAt = DateTimeOffset.UtcNow;
        memory.IsOutdated = text is not null;
        memory.Revision++;
        await db.Embeddings.Where(x => x.MemoryId == id).ExecuteDeleteAsync(ct);
        await InvalidateMemoryContextAsync(db, memory.BotId, [id], ct);
        var result = memory;
        if (text is not null)
        {
            result = new Memory
            {
                BotId = memory.BotId, SubjectId = actor, FactKey = memory.FactKey,
                FactKeyHash = memory.FactKeyHash, Text = text, SupersedesMemoryId = id,
                // Manual correction is current evidence, not a replay of the previous user messages.
                LastConfirmedAt = DateTimeOffset.UtcNow, EvidenceJson = "[]"
            };
            db.Memories.Add(result);
            Jobs.Add(db, memory.BotId, JobKind.Embedding, new IdPayload(result.Id));
        }
        if (!user.RetainMemoryHistory)
        {
            memory.FactKeyHash = Json.Hash(memory.FactKey);
            memory.Text = memory.FactKey = "";
            memory.EvidenceJson = "[]";
            await MemoryMaintenanceWorkflow.ScrubAuditAsync(db, actor, [id], ct);
        }
        var operation = new MemoryOperation
        {
            ActorId = actor, BotId = memory.BotId, IdempotencyKey = key, ArgumentsHash = hash,
            Kind = kind, TargetMemoryId = id, ExpectedRevision = expectedRevision,
            ResultMemoryId = result.Id, ResultRevision = result.Revision
        };
        db.MemoryOperations.Add(operation);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Receipt(operation);
    }

    private static MemoryOperationReceipt Receipt(MemoryOperation operation) => new(operation.Id, operation.Kind,
        operation.TargetMemoryId, operation.ResultMemoryId, operation.ResultRevision,
        operation.Kind == "Edit" ? "Current" : "Archived");

    internal static async Task ValidateFamilyAsync(WonderDbContext db, Memory selected, CancellationToken ct)
    {
        var rows = await db.Memories.AsNoTracking().Where(x => x.BotId == selected.BotId &&
            x.SubjectId == selected.SubjectId).ToListAsync(ct);
        var visited = new HashSet<Guid>();
        var cursor = selected;
        while (true)
        {
            if (!visited.Add(cursor.Id)) throw new DbUpdateConcurrencyException("Cyclic memory lineage.");
            if (cursor.Id != selected.Id && cursor.Status != MemoryStatus.Revoked && cursor.ArchivedAt is null)
                throw new DbUpdateConcurrencyException("Multiple current versions in memory lineage.");
            var children = rows.Where(x => x.SupersedesMemoryId == cursor.Id).ToArray();
            if (children.Length > 1 || (cursor.Id == selected.Id && children.Length != 0))
                throw new DbUpdateConcurrencyException("Ambiguous memory lineage.");
            if (cursor.SupersedesMemoryId is not { } parent) break;
            cursor = rows.SingleOrDefault(x => x.Id == parent)
                     ?? throw new DbUpdateConcurrencyException("Broken memory lineage.");
        }
    }
}
