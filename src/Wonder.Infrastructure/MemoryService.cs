using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Wonder.AI;
using Wonder.Core;
using Wonder.Memory;

namespace Wonder.Infrastructure;

public sealed class MemoryService(
    IDbContextFactory<WonderDbContext> factory,
    ModelRouter models,
    IConfiguration configuration)
{
    public async Task<IReadOnlyList<Core.Memory>> RetrieveAsync(MemoryAccess access, string query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var resolved = await ResolveAccessAsync(db, access, ct);
        if (resolved is null) return [];
        access = resolved;
        var allowed = MemoryQueries.Authorized(db, access);
        if (!await allowed.AnyAsync(ct)) return [];
        var profile = await db.Models.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BotId == access.BotId && x.TaskKind == TaskKind.Embedding, ct);
        if (profile is null) return await allowed.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
        var space = await db.EmbeddingProfiles.AsNoTracking().SingleOrDefaultAsync(
            x => x.BotId == access.BotId && x.Provider == profile.Endpoint && x.Model == profile.Model &&
                 x.Dimensions == profile.EmbeddingDimensions, ct);
        if (space is null) return await allowed.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
        float[] vector;
        try
        {
            vector = await models.EmbedAsync(profile, query, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or ModelConfigurationException or TaskCanceledException &&
                                   !ct.IsCancellationRequested)
        {
            return await allowed.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
        }

        return await MemoryQueries.Nearest(db, access, space.Id, vector).ToListAsync(ct);
    }

    public static async Task<MemoryAccess?> ResolveAccessAsync(WonderDbContext db, MemoryAccess access, CancellationToken ct)
    {
        var user = await db.LogicalUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == access.UserId, ct);
        if (user is null) return null;
        Guid? space = null;
        if (access.GroupId is not null)
            space = await (from g in db.GroupPolicies
                join s in db.ContextSpaces on g.ContextSpaceId equals (Guid?)s.Id
                where g.BotId == access.BotId && g.GroupId == access.GroupId && s.BotId == access.BotId
                select (Guid?)s.Id).SingleOrDefaultAsync(ct);
        return access with { ContextSpaceId = space, AllowUserShared = user.AllowMemory && user.AllowCrossGroupMemory, AllowPersonal = user.AllowMemory };
    }

    public async Task<Guid> ShareAsync(Guid subjectId, Guid memoryId, MemoryScope scope, CancellationToken ct, Guid? expectedSpaceId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var source = await db.Memories.SingleOrDefaultAsync(x => x.Id == memoryId && x.SubjectId == subjectId &&
            x.SharedFromMemoryId == null && x.Status != MemoryStatus.Revoked, ct)
            ?? throw new KeyNotFoundException("Memory not found.");
        var user = await db.LogicalUsers.SingleAsync(x => x.Id == subjectId, ct);
        if (!user.AllowMemory || !user.AllowCrossGroupMemory)
            throw new ArgumentException("Enable memory and cross-group sharing first.");
        if (source.Scope is not (MemoryScope.Private or MemoryScope.GroupUser))
            throw new ArgumentException("Only personal source memories can be shared.");
        string owner;
        if (scope == MemoryScope.UserShared) owner = subjectId.ToString();
        else if (scope == MemoryScope.SpaceShared && source.Scope == MemoryScope.GroupUser)
        {
            var space = await (from g in db.GroupPolicies
                join s in db.ContextSpaces on g.ContextSpaceId equals (Guid?)s.Id
                where g.BotId == source.BotId && g.GroupId == source.ScopeOwnerId && s.BotId == source.BotId
                select (Guid?)s.Id).SingleOrDefaultAsync(ct);
            if (space is null || expectedSpaceId != space)
                throw new ArgumentException("The source group shared space changed; reload and select it again.");
            owner = space.Value.ToString();
        }
        else throw new ArgumentException("Select UserShared or the source group's SpaceShared scope.");
        var existing = await db.Memories.SingleOrDefaultAsync(x => x.SharedFromMemoryId == source.Id &&
            x.Scope == scope && x.ScopeOwnerId == owner && x.Status != MemoryStatus.Revoked, ct);
        if (existing is not null) return existing.Id;
        var copy = new Core.Memory { BotId = source.BotId, SubjectId = subjectId, Scope = scope, ScopeOwnerId = owner,
            SharedFromMemoryId = source.Id, FactKey = source.FactKey, Text = source.Text,
            SourceReflectionId = source.SourceReflectionId, EvidenceJson = source.EvidenceJson };
        db.Memories.Add(copy);
        Jobs.Add(db, source.BotId, JobTypes.Embedding, new IdPayload(copy.Id));
        await db.Bots.Where(x => x.Id == source.BotId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SettingsRevision, x => x.SettingsRevision + 1), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return copy.Id;
    }

    private static void RevokeRows(WonderDbContext db, IEnumerable<Core.Memory> rows, LogicalUser user)
    {
        foreach (var memory in rows)
        {
            db.Revocations.Add(new MemoryRevocation { BotId = memory.BotId, UserId = user.Id, Scope = memory.Scope,
                ScopeOwnerId = memory.ScopeOwnerId, FactKeyHash = Json.Hash(memory.FactKey), Epoch = user.RevocationEpoch });
            memory.Status = MemoryStatus.Revoked;
            memory.Text = "";
            memory.FactKey = "";
            memory.EvidenceJson = "[]";
        }
    }

    public async Task WithdrawAsync(Guid subjectId, Guid sharedId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var memory = await db.Memories.SingleOrDefaultAsync(x => x.Id == sharedId && x.SubjectId == subjectId &&
            x.SharedFromMemoryId != null && x.Status != MemoryStatus.Revoked, ct)
            ?? throw new KeyNotFoundException("Shared memory not found.");
        var user = await db.LogicalUsers.SingleAsync(x => x.Id == subjectId, ct);
        user.RevocationEpoch++;
        RevokeRows(db, [memory], user);
        await db.Embeddings.Where(x => x.MemoryId == memory.Id).ExecuteDeleteAsync(ct);
        await InvalidateSharedContextAsync(db, memory.BotId, [memory.Id], ct);
        if (!string.IsNullOrWhiteSpace(configuration["Backups:AgeRecipient"])) Jobs.Add(db, memory.BotId, JobTypes.Ledger, new IdPayload(user.Id));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task UpdateSettingsAsync(Guid subjectId, bool allowMemory, bool? allowCrossGroup, string? preferredName, CancellationToken ct)
    {
        if (preferredName is not null && preferredName.Trim().Length > 100) throw new ArgumentException("Name is too long.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await db.LogicalUsers.SingleAsync(x => x.Id == subjectId, ct);
        user.AllowMemory = allowMemory;
        user.AllowCrossGroupMemory = allowMemory && (allowCrossGroup ?? user.AllowCrossGroupMemory);
        if (preferredName is not null) user.PreferredName = preferredName.Trim();
        user.RevocationEpoch++;
        if (!user.AllowCrossGroupMemory)
        {
            var copies = await db.Memories.Where(x => x.SubjectId == subjectId && x.SharedFromMemoryId != null &&
                x.Status != MemoryStatus.Revoked).ToListAsync(ct);
            RevokeRows(db, copies, user);
            var ids = copies.Select(x => x.Id).ToArray();
            await db.Embeddings.Where(x => ids.Contains(x.MemoryId)).ExecuteDeleteAsync(ct);
            foreach (var bot in copies.Select(x => x.BotId).Distinct())
            {
                await InvalidateSharedContextAsync(db, bot, copies.Where(x => x.BotId == bot).Select(x => x.Id).ToArray(), ct);
                if (!string.IsNullOrWhiteSpace(configuration["Backups:AgeRecipient"])) Jobs.Add(db, bot, JobTypes.Ledger, new IdPayload(user.Id));
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static async Task InvalidateSharedContextAsync(WonderDbContext db, Guid botId, IReadOnlyCollection<Guid> memoryIds, CancellationToken ct)
    {
        if (memoryIds.Count == 0) return;
        await db.Bots.Where(x => x.Id == botId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SettingsRevision, x => x.SettingsRevision + 1), ct);
        var candidates = await db.Messages.AsNoTracking().Where(x => x.BotId == botId && x.Role == "assistant" &&
            !x.ContextInvalidated && x.ContextSnapshotJson != null).Select(x => new
            { x.Id, x.SessionId, x.ExternalMessageId, x.ContextSnapshotJson }).ToListAsync(ct);
        var affected = candidates.Where(x => MayReferenceMemory(x.ContextSnapshotJson!, memoryIds, snapshot: true)).ToArray();
        var messages = affected.Select(x => x.Id).ToArray();
        var sessions = affected.Where(x => x.SessionId != null).Select(x => x.SessionId!.Value).ToArray();
        await db.Messages.Where(x => messages.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        var summaries = await db.Summaries.AsNoTracking().Where(x => x.BotId == botId && !x.ContextInvalidated)
            .Select(x => new { x.Id, x.MemoryIdsJson }).ToListAsync(ct);
        var summaryIds = summaries.Where(x => MayReferenceMemory(x.MemoryIdsJson, memoryIds, snapshot: false))
            .Select(x => x.Id).ToArray();
        await db.Summaries.Where(x => x.BotId == botId && (sessions.Contains(x.SessionId) || summaryIds.Contains(x.Id)))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        var sourceJobs = affected.Where(x => x.ExternalMessageId.StartsWith("generated:", StringComparison.Ordinal))
            .Select(x => Guid.Parse(x.ExternalMessageId[10..])).ToArray();
        var batches = db.Batches.Where(x => x.BotId == botId && sourceJobs.Contains(x.SourceJobId)).Select(x => x.Id);
        await db.Parts.Where(x => batches.Contains(x.BatchId) && x.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeliveryStatus.Failed), ct);
    }

    private static bool MayReferenceMemory(string json, IReadOnlyCollection<Guid> memoryIds, bool snapshot)
    {
        // Missing/corrupt provenance must not block revocation or leave this cache usable.
        // Only the affected cache row is invalidated; its original text is retained.
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            IReadOnlyList<Guid>? ids = snapshot
                ? Json.Read<ContextSnapshot>(json).RetrievedMemoryIds
                : Json.Read<Guid[]>(json);
            return ids is null || ids.Intersect(memoryIds).Any();
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public async Task RevokeAsync(Guid userId, Guid memoryId, string? replacement, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var selected = await db.Memories.SingleOrDefaultAsync(x => x.Id == memoryId && x.SubjectId == userId &&
            x.Status != MemoryStatus.Revoked, ct) ?? throw new KeyNotFoundException("Memory not found.");
        var rootId = selected.SharedFromMemoryId ?? selected.Id;
        var memory = await db.Memories.SingleAsync(x => x.Id == rootId && x.SubjectId == userId, ct);
        var related = await db.Memories.Where(x => x.SubjectId == userId && (x.Id == rootId || x.SharedFromMemoryId == rootId) &&
            x.Status != MemoryStatus.Revoked).ToListAsync(ct);
        var user = await db.LogicalUsers.SingleAsync(x => x.Id == userId, ct);
        user.RevocationEpoch++;
        var factKey = memory.FactKey;
        var relatedIds = related.Select(x => x.Id).ToArray();
        RevokeRows(db, related, user);
        await db.Embeddings.Where(x => relatedIds.Contains(x.MemoryId)).ExecuteDeleteAsync(ct);
        await InvalidateSharedContextAsync(db, memory.BotId, relatedIds, ct);
        await ClearDerivedAsync(db, memory.BotId, userId, ct);
        if (!string.IsNullOrWhiteSpace(configuration["Backups:AgeRecipient"]))
            Jobs.Add(db, memory.BotId, JobTypes.Ledger, new IdPayload(userId));
        if (replacement is { Length: > 0 })
        {
            var corrected = new Core.Memory
            {
                BotId = memory.BotId,
                SubjectId = userId,
                Scope = memory.Scope,
                ScopeOwnerId = memory.ScopeOwnerId,
                FactKey = factKey,
                Text = replacement,
                SourceReflectionId = Guid.Empty
            };
            db.Memories.Add(corrected);
            Jobs.Add(db, memory.BotId, JobTypes.Embedding, new IdPayload(corrected.Id));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static async Task ClearDerivedAsync(WonderDbContext db, Guid botId, Guid userId, CancellationToken ct)
    {
        await db.Relationships.Where(x => x.BotId == botId && x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Observations.Where(x => x.BotId == botId && x.UserId == userId).ExecuteDeleteAsync(ct);
        var messageIds = await db.Messages.Where(x => x.BotId == botId && x.SenderId == userId).Select(x => x.Id)
            .ToListAsync(ct);
        var sessionIds = await db.Messages.Where(x => messageIds.Contains(x.Id) && x.SessionId != null)
            .Select(x => x.SessionId!.Value).Distinct().ToListAsync(ct);
        // New sessions can depend on older messages without the original author posting again.
        var originalReplies = await db.Messages.Where(x => x.BotId == botId && x.SessionId != null &&
            sessionIds.Contains(x.SessionId.Value) && x.Role == "assistant").Select(x => x.Id).ToListAsync(ct);
        var roots = messageIds.Concat(originalReplies).ToHashSet();
        var cached = await db.Messages.AsNoTracking().Where(x => x.BotId == botId && x.Role == "assistant" &&
            !x.ContextInvalidated && x.ContextSnapshotJson != null)
            .Select(x => new { x.Id, x.ExternalMessageId, x.ContextSnapshotJson }).ToListAsync(ct);
        var dependent = cached.Where(x =>
        {
            try
            {
                var snapshot = Json.Read<ContextSnapshot>(x.ContextSnapshotJson!);
                return snapshot.SourceUserIds?.Contains(userId) == true || snapshot.SourceMessageIds?.Any(roots.Contains) == true;
            }
            catch (JsonException) { return true; }
        }).ToArray();
        var dependentIds = dependent.Select(x => x.Id).ToArray();
        await db.Messages.Where(x => dependentIds.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        var cachedSummaries = await db.Summaries.AsNoTracking().Where(x => x.BotId == botId && !x.ContextInvalidated)
            .Select(x => new { x.Id, x.MessageIdsJson }).ToListAsync(ct);
        var dependentSummaries = cachedSummaries.Where(x => MayReferenceMemory(x.MessageIdsJson, roots, snapshot: false))
            .Select(x => x.Id).ToArray();
        await db.Summaries.Where(x => dependentSummaries.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        var sourceJobs = dependent.Select(x => x.ExternalMessageId.StartsWith("generated:", StringComparison.Ordinal) &&
            Guid.TryParse(x.ExternalMessageId[10..], out var sourceJob) ? sourceJob : Guid.Empty).Where(x => x != Guid.Empty).ToArray();
        var dependentBatches = db.Batches.Where(x => x.BotId == botId && sourceJobs.Contains(x.SourceJobId)).Select(x => x.Id);
        await db.Parts.Where(x => dependentBatches.Contains(x.BatchId) && x.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeliveryStatus.Failed), ct);
        var subjectKey = userId.ToString();
        await db.Summaries
            .Where(x => x.BotId == botId && (sessionIds.Contains(x.SessionId) || x.SubjectIdsJson.Contains(subjectKey)))
            .ExecuteDeleteAsync(ct);
        await db.Reflections.Where(x => x.BotId == botId && x.SubjectId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"), ct);
        await db.Messages.Where(x => messageIds.Contains(x.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.AllowReflection, false).SetProperty(x => x.ContextInvalidated, true)
            .SetProperty(x => x.Text, "[user content removed]").SetProperty(x => x.SegmentsJson, "[]"), ct);
        await db.Messages.Where(x =>
                x.SessionId != null && sessionIds.Contains(x.SessionId.Value) && x.Role == "assistant")
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Text, "[derived content removed]").SetProperty(x => x.SegmentsJson, "[]")
                    .SetProperty(x => x.ContextSnapshotJson, (string?)null), ct);
        var batches = await db.Batches.Where(x => sessionIds.Contains(x.SessionId)).Select(x => x.Id).ToListAsync(ct);
        await db.Parts.Where(x => batches.Contains(x.BatchId)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "")
            .SetProperty(x => x.Status,
                x => x.Status == DeliveryStatus.Pending ? DeliveryStatus.Failed :
                    x.Status == DeliveryStatus.Sending ? DeliveryStatus.DeliveryUnknown : x.Status), ct);
        var assets = await db.Media.Where(x => messageIds.Contains(x.MessageId)).ToListAsync(ct);
        foreach (var asset in assets)
        {
            if (File.Exists(asset.Path)) File.Delete(asset.Path);
            db.Media.Remove(asset);
        }
    }

    public async Task ReplayLedgerAsync(string file, CancellationToken ct)
    {
        var ledger = Json.Read<MemoryRevocation[]>(await File.ReadAllTextAsync(file, ct));
        foreach (var item in ledger)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var user = await db.LogicalUsers.SingleOrDefaultAsync(x => x.Id == item.UserId, ct);
            if (user is null || !await db.Bots.AnyAsync(x => x.Id == item.BotId, ct)) continue;
            user.RevocationEpoch = Math.Max(user.RevocationEpoch, item.Epoch);
            if (!await db.Revocations.AnyAsync(x => x.Id == item.Id, ct)) db.Revocations.Add(item);
            var candidates = await db.Memories.Where(x =>
                x.BotId == item.BotId && x.SubjectId == item.UserId && x.Scope == item.Scope &&
                x.ScopeOwnerId == item.ScopeOwnerId && x.CreatedAt <= item.CreatedAt &&
                x.Status != MemoryStatus.Revoked).ToListAsync(ct);
            var revoked = candidates.Where(x => Json.Hash(x.FactKey) == item.FactKeyHash).ToArray();
            foreach (var memory in revoked)
            {
                memory.Status = MemoryStatus.Revoked;
                memory.Text = "";
                memory.FactKey = "";
                memory.EvidenceJson = "[]";
                await db.Embeddings.Where(x => x.MemoryId == memory.Id).ExecuteDeleteAsync(ct);
            }

            var revokedIds = revoked.Select(x => x.Id).ToArray();
            var copies = await db.Memories.Where(x => x.SharedFromMemoryId != null && revokedIds.Contains(x.SharedFromMemoryId.Value))
                .Select(x => x.Id).ToListAsync(ct);
            await InvalidateSharedContextAsync(db, item.BotId, revokedIds.Concat(copies).ToArray(), ct);
            await ClearDerivedAsync(db, item.BotId, item.UserId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}