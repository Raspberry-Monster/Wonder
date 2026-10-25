using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;

namespace Wonder.Host;

public sealed class MemoryService(
    IDbContextFactory<WonderDbContext> factory,
    ModelRouter models,
    IConfiguration configuration)
{
    public async Task<IReadOnlyList<Memory>> RetrieveAsync(MemoryAccess access, string query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var resolved = await ResolveAccessAsync(db, access, ct);
        if (resolved is null) return [];
        access = resolved;
        var allowed = MemoryQueries.Authorized(db, access);
        if (!await allowed.AnyAsync(ct)) return [];
        var profile = await db.Models.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BotId == access.BotId && x.TaskKind == TaskKind.Embedding, ct);
        if (profile is null)
            return await allowed.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
        var space = await db.EmbeddingProfiles.AsNoTracking().SingleOrDefaultAsync(
            x => x.BotId == access.BotId && x.Provider == profile.Endpoint && x.Model == profile.Model &&
                 x.Dimensions == profile.EmbeddingDimensions && x.Version == 1, ct);
        if (space is null)
            return await allowed.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
        var ranked = new Dictionary<Guid, (Memory Memory, double Score)>();
        try
        {
            foreach (var part in models.Context(profile).Split(query))
            {
                var vector = await models.EmbedAsync(profile, part, ct);
                var matches = await MemoryQueries.Nearest(db, access, space.Id, vector).ToListAsync(ct);
                for (var i = 0; i < matches.Count; i++)
                {
                    var match = matches[i];
                    ranked[match.Id] = (match, ranked.GetValueOrDefault(match.Id).Score + 1d / (60 + i + 1));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or ModelConfigurationException or TokenQuotaException
                                       or ContextLimitException or TaskCanceledException &&
                                   !ct.IsCancellationRequested)
        {
            return await allowed.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
        }

        return ranked.Values.OrderByDescending(x => x.Score).ThenBy(x => x.Memory.Id).Take(5).Select(x => x.Memory)
            .ToArray();
    }

    public static async Task<MemoryAccess?> ResolveAccessAsync(WonderDbContext db, MemoryAccess access,
        CancellationToken ct)
    {
        var user = await db.LogicalUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == access.UserId, ct);
        if (user is null) return null;
        return access with { AllowPersonal = user.AllowMemory };
    }

    private static void RevokeRows(WonderDbContext db, IEnumerable<Memory> rows, LogicalUser user)
    {
        foreach (var memory in rows)
        {
            db.Revocations.Add(new MemoryRevocation
            {
                BotId = memory.BotId, UserId = user.Id, ResourceId = memory.Id,
                FactKeyHash = string.IsNullOrEmpty(memory.FactKeyHash) ? Json.Hash(memory.FactKey) : memory.FactKeyHash,
                Epoch = user.RevocationEpoch
            });
            memory.Status = MemoryStatus.Revoked;
            memory.Text = "";
            memory.FactKey = "";
            memory.EvidenceJson = "[]";
        }
    }

    public async Task UpdateSettingsAsync(Guid subjectId, bool allowMemory,
        string? preferredName, CancellationToken ct)
    {
        await UpdateSettingsAsync(subjectId, allowMemory, preferredName, null, null, null, null, ct);
    }

    public async Task UpdateSettingsAsync(Guid subjectId, bool allowMemory,
        string? preferredName,
        bool? allowMerge, bool? allowUpdate, bool? allowArchive, bool? retainHistory, CancellationToken ct)
    {
        if (preferredName is not null && preferredName.Trim().Length > 100)
            throw new ArgumentException("Name is too long.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await db.LogicalUsers.SingleAsync(x => x.Id == subjectId, ct);
        if (!await UserFence.CheckAsync(db, subjectId, user.RevocationEpoch, ct))
            throw new DbUpdateConcurrencyException("Memory policy changed; reload settings.");
        user.AllowMemory = allowMemory;
        user.AllowMemoryMerge = allowMerge ?? user.AllowMemoryMerge;
        user.AllowMemoryUpdate = allowUpdate ?? user.AllowMemoryUpdate;
        user.AllowMemoryArchive = allowArchive ?? user.AllowMemoryArchive;
        user.RetainMemoryHistory = retainHistory ?? user.RetainMemoryHistory;
        if (preferredName is not null) user.PreferredName = preferredName.Trim();
        user.RevocationEpoch++;
        await db.MemoryCandidates.Where(x => x.SubjectId == subjectId && x.Status == MemoryMaintenanceStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MemoryMaintenanceStatus.Cancelled)
                .SetProperty(x => x.Text, "").SetProperty(x => x.FactKey, "").SetProperty(x => x.EvidenceJson, "[]")
                .SetProperty(x => x.Reason, ""), ct);
        if (!user.RetainMemoryHistory)
        {
            var history = await db.Memories.Where(x => x.SubjectId == subjectId &&
                                                       x.ArchivedAt != null && x.Status != MemoryStatus.Revoked)
                .ToListAsync(ct);
            foreach (var row in history)
            {
                if (row.FactKey.Length > 0) row.FactKeyHash = Json.Hash(row.FactKey);
                row.Text = row.FactKey = "";
                row.EvidenceJson = "[]";
                row.Revision++;
            }

            await MemoryMaintenanceWorkflow.ScrubAuditAsync(db, subjectId, history.Select(x => x.Id).ToArray(), ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static async Task InvalidateMemoryContextAsync(WonderDbContext db, Guid botId,
        IReadOnlyCollection<Guid> memoryIds, CancellationToken ct)
    {
        if (memoryIds.Count == 0) return;
        var noteSources = await db.Notes.AsNoTracking().Where(x => x.BotId == botId && !x.PrivacyDeleted)
            .Select(x => new { x.Id, x.SourceMemoryIdsJson }).ToArrayAsync(ct);
        var invalidNotes = noteSources.Where(x =>
            !ConversationHistory.TryMemoryIds(x.SourceMemoryIdsJson, false, out var ids) || ids.Any(memoryIds.Contains))
            .Select(x => x.Id).ToArray();
        await db.Notes.Where(x => invalidNotes.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceInvalidated, true), ct);
        await NoteSources.InvalidateAsync(db, invalidNotes, false, ct);
        await db.Bots.Where(x => x.Id == botId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SettingsRevision, x => x.SettingsRevision + 1), ct);
        var candidates = await db.Messages.AsNoTracking().Where(x => x.BotId == botId && x.Role == MessageRole.Assistant &&
                                                                     !x.ContextInvalidated &&
                                                                     x.ContextSnapshotJson != null).Select(x => new
            { x.Id, x.SessionId, x.ExternalMessageId, x.ContextSnapshotJson }).ToListAsync(ct);
        var affected = candidates.Where(x => MayReferenceMemory(x.ContextSnapshotJson!, memoryIds, true)).ToArray();
        var messages = affected.Select(x => x.Id).ToArray();
        var sessions = affected.Where(x => x.SessionId != null).Select(x => x.SessionId!.Value).ToArray();
        await db.Messages.Where(x => messages.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        var summaries = await db.Summaries.AsNoTracking().Where(x => x.BotId == botId && !x.ContextInvalidated)
            .Select(x => new { x.Id, x.MemoryIdsJson }).ToListAsync(ct);
        var summaryIds = summaries.Where(x => MayReferenceMemory(x.MemoryIdsJson, memoryIds, false))
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
            var ids = snapshot
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
                                                                   x.Status != MemoryStatus.Revoked, ct) ??
                       throw new KeyNotFoundException("Memory not found.");
        var rootId = selected.Id;
        var memory = await db.Memories.SingleAsync(x => x.Id == rootId && x.SubjectId == userId, ct);
        if (!string.IsNullOrWhiteSpace(replacement))
        {
            var embedding = await db.Models.AsNoTracking()
                .SingleOrDefaultAsync(x => x.BotId == memory.BotId && x.TaskKind == TaskKind.Embedding, ct);
            if (embedding is not null &&
                models.Context(embedding).Text(replacement) > models.Context(embedding).InputLimit)
                throw new ArgumentException(
                    "The replacement exceeds the embedding input limit. Split it into shorter facts.",
                    nameof(replacement));
        }

        // User deletion/correction covers the complete private version family.
        var roots = await db.Memories.Where(x => x.SubjectId == userId && x.BotId == memory.BotId).ToListAsync(ct);
        var family = new HashSet<Guid> { rootId };
        bool expanded;
        do
        {
            expanded = false;
            foreach (var row in roots.Where(x => x.SupersedesMemoryId != null))
                if (family.Contains(row.Id) || family.Contains(row.SupersedesMemoryId!.Value))
                {
                    expanded |= family.Add(row.Id);
                    expanded |= family.Add(row.SupersedesMemoryId!.Value);
                }
        } while (expanded);

        var related = await db.Memories.Where(x => x.SubjectId == userId && x.BotId == memory.BotId &&
                                                   family.Contains(x.Id) &&
                                                   x.Status != MemoryStatus.Revoked).ToListAsync(ct);
        var user = await db.LogicalUsers.SingleAsync(x => x.Id == userId, ct);
        if (!await UserFence.CheckAsync(db, userId, user.RevocationEpoch, ct))
            throw new DbUpdateConcurrencyException("Memory changed; retry revocation.");
        user.RevocationEpoch++;
        var factKey = memory.FactKey;
        var relatedIds = related.Select(x => x.Id).ToArray();
        RevokeRows(db, related, user);
        await db.Embeddings.Where(x => relatedIds.Contains(x.MemoryId)).ExecuteDeleteAsync(ct);
        await InvalidateMemoryContextAsync(db, memory.BotId, relatedIds, ct);
        await ClearDerivedAsync(db, memory.BotId, userId, ct);
        if (!string.IsNullOrWhiteSpace(configuration["Backups:AgeRecipient"]))
            Jobs.Add(db, memory.BotId, JobKind.Ledger, new IdPayload(userId));
        if (replacement is { Length: > 0 })
        {
            var corrected = new Memory
            {
                BotId = memory.BotId,
                SubjectId = userId,
                FactKey = factKey,
                SupersedesMemoryId = memory.Id,
                Text = replacement,
                SourceReflectionId = Guid.Empty
            };
            db.Memories.Add(corrected);
            Jobs.Add(db, memory.BotId, JobKind.Embedding, new IdPayload(corrected.Id));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static async Task ClearDerivedAsync(WonderDbContext db, Guid botId, Guid userId, CancellationToken ct)
    {
        await db.AgentReflectionRequests.Where(x => x.BotId == botId && x.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Reason, ""), ct);
        await db.ReflectionGenerations.Where(x => x.BotId == botId && x.SubjectId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ReflectionGenerationStatus.Cancelled).SetProperty(x => x.CancellationReason, ReflectionCancellationReason.PrivacyRevocation), ct);
        await db.MemoryCandidates.Where(x => x.BotId == botId && x.SubjectId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "").SetProperty(x => x.FactKey, "")
                .SetProperty(x => x.EvidenceJson, "[]").SetProperty(x => x.Reason, "")
                .SetProperty(x => x.Status, MemoryMaintenanceStatus.Cancelled), ct);
        await db.Relationships.Where(x => x.BotId == botId && x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Observations.Where(x => x.BotId == botId && x.UserId == userId).ExecuteDeleteAsync(ct);
        var messageIds = await db.Messages.Where(x => x.BotId == botId && x.SenderId == userId).Select(x => x.Id)
            .ToListAsync(ct);
        var sessionIds = await db.Messages.Where(x => messageIds.Contains(x.Id) && x.SessionId != null)
            .Select(x => x.SessionId!.Value).Distinct().ToListAsync(ct);
        // New sessions can depend on older messages without the original author posting again.
        var originalReplies = await db.Messages.Where(x => x.BotId == botId && x.SessionId != null &&
                                                           sessionIds.Contains(x.SessionId.Value) &&
                                                           x.Role == MessageRole.Assistant).Select(x => x.Id).ToListAsync(ct);
        var roots = messageIds.Concat(originalReplies).ToHashSet();
        var cached = await db.Messages.AsNoTracking().Where(x => x.BotId == botId && x.Role == MessageRole.Assistant &&
                                                                 !x.ContextInvalidated && x.ContextSnapshotJson != null)
            .Select(x => new { x.Id, x.ExternalMessageId, x.ContextSnapshotJson }).ToListAsync(ct);
        var dependent = cached.Where(x =>
        {
            try
            {
                var snapshot = Json.Read<ContextSnapshot>(x.ContextSnapshotJson!);
                return snapshot.SourceUserIds?.Contains(userId) == true ||
                       snapshot.SourceMessageIds?.Any(roots.Contains) == true;
            }
            catch (JsonException)
            {
                return true;
            }
        }).ToArray();
        var dependentIds = dependent.Select(x => x.Id).ToArray();
        await db.Messages.Where(x => dependentIds.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        var cachedSummaries = await db.Summaries.AsNoTracking().Where(x => x.BotId == botId && !x.ContextInvalidated)
            .Select(x => new { x.Id, x.MessageIdsJson }).ToListAsync(ct);
        var dependentSummaries = cachedSummaries.Where(x => MayReferenceMemory(x.MessageIdsJson, roots, false))
            .Select(x => x.Id).ToArray();
        await db.Summaries.Where(x => dependentSummaries.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        var sourceJobs = dependent.Select(x => x.ExternalMessageId.StartsWith("generated:", StringComparison.Ordinal) &&
                                               Guid.TryParse(x.ExternalMessageId[10..], out var sourceJob)
            ? sourceJob
            : Guid.Empty).Where(x => x != Guid.Empty).ToArray();
        var dependentBatches = db.Batches.Where(x => x.BotId == botId && sourceJobs.Contains(x.SourceJobId))
            .Select(x => x.Id);
        await db.Parts.Where(x => dependentBatches.Contains(x.BatchId) && x.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeliveryStatus.Failed), ct);
        var subjectKey = userId.ToString();
        // Drafts can carry user-derived text. Reset them when their subject is revoked.
        await db.Outbox.Where(x => x.BotId == botId && x.ProgressJson != null &&
                                   (x.ProgressJson.Contains(subjectKey) || x.Payload.Contains(subjectKey) ||
                                    x.Type == JobKind.MemoryMaintenance))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProgressJson, (string?)null), ct);
        await db.Summaries
            .Where(x => x.BotId == botId && (sessionIds.Contains(x.SessionId) || x.SubjectIdsJson.Contains(subjectKey)))
            .ExecuteDeleteAsync(ct);
        await db.Reflections.Where(x => x.BotId == botId && x.SubjectId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"), ct);
        await db.Messages.Where(x => messageIds.Contains(x.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.AllowReflection, false).SetProperty(x => x.ContextInvalidated, true)
            .SetProperty(x => x.Text, "[user content removed]").SetProperty(x => x.SegmentsJson, "[]"), ct);
        await db.Messages.Where(x =>
                x.SessionId != null && sessionIds.Contains(x.SessionId.Value) && x.Role == MessageRole.Assistant)
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
        var ledger = DeletionLedger.Read(await File.ReadAllTextAsync(file, ct));
        foreach (var item in ledger)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var user = await db.LogicalUsers.SingleOrDefaultAsync(x => x.Id == item.UserId, ct);
            if (user is null || !await db.Bots.AnyAsync(x => x.Id == item.BotId, ct)) continue;
            if (!await UserFence.CheckAsync(db, user.Id, user.RevocationEpoch, ct))
                throw new DbUpdateConcurrencyException("User changed during ledger replay; retry.");
            if (item.ResourceKind == DeletionResourceKind.Note)
            {
                if (item.ResourceId is null)
                    throw new InvalidOperationException("Unsupported Notes deletion ledger target.");
                user.NotesEpoch = Math.Max(user.NotesEpoch, item.Epoch);
                if (!await db.Revocations.AnyAsync(x => x.Id == item.Id, ct)) db.Revocations.Add(item);
                await NoteSources.PurgeAsync(db, item.ResourceId.Value, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                continue;
            }
            user.RevocationEpoch = Math.Max(user.RevocationEpoch, item.Epoch);
            if (!await db.Revocations.AnyAsync(x => x.Id == item.Id, ct)) db.Revocations.Add(item);
            var candidates = await db.Memories.Where(x =>
                x.BotId == item.BotId && x.SubjectId == item.UserId && item.ResourceKind == DeletionResourceKind.Memory &&
                (item.ResourceId == null || x.Id == item.ResourceId) && x.CreatedAt <= item.CreatedAt &&
                x.Status != MemoryStatus.Revoked).ToListAsync(ct);
            var revoked = candidates.Where(x =>
                    (string.IsNullOrEmpty(x.FactKeyHash) ? Json.Hash(x.FactKey) : x.FactKeyHash) == item.FactKeyHash)
                .ToArray();
            foreach (var memory in revoked)
            {
                memory.Status = MemoryStatus.Revoked;
                memory.Text = "";
                memory.FactKey = "";
                memory.EvidenceJson = "[]";
                await db.Embeddings.Where(x => x.MemoryId == memory.Id).ExecuteDeleteAsync(ct);
            }

            var revokedIds = revoked.Select(x => x.Id).ToArray();
            await InvalidateMemoryContextAsync(db, item.BotId, revokedIds, ct);
            await ClearDerivedAsync(db, item.BotId, item.UserId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
