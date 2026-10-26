using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;

namespace Wonder.Host;

public sealed partial class MemoryService(
    IDbContextFactory<WonderDbContext> factory,
    ModelRouter models)
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
        catch (Exception ex) when (ex is HttpRequestException or ModelConfigurationException or ModelResponseException or TokenQuotaException
                                       or ContextLimitException or TaskCanceledException &&
                                   !ct.IsCancellationRequested)
        {
            return await allowed.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
        }

        var unindexed = await allowed.Where(m => !db.Embeddings.Any(e => e.MemoryId == m.Id &&
                e.ProfileId == space.Id && e.SourceRevision == m.Revision))
            .OrderByDescending(m => m.CreatedAt).ThenBy(m => m.Id).Take(5).ToListAsync(ct);
        return unindexed.Concat(ranked.Values.OrderByDescending(x => x.Score).ThenBy(x => x.Memory.Id).Take(5).Select(x => x.Memory)).DistinctBy(x => x.Id).Take(5).ToArray();
    }

    public static async Task<MemoryAccess?> ResolveAccessAsync(WonderDbContext db, MemoryAccess access,
        CancellationToken ct)
    {
        var user = await db.LogicalUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == access.UserId, ct);
        if (user is null) return null;
        return access with { AllowPersonal = user.AllowMemory };
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
        await ContentSources.InvalidateMemoriesAsync(db, botId, memoryIds, ct);
        await RelationshipClaims.InvalidateAsync(db, botId, memoryIds, [], ct);
    }

    // The old entry point performed privacy deletion even for correction. It must never run.
    // Forget requires Gate 2 cleanup and independently verified recovery protection.
    public Task RevokeAsync(Guid userId, Guid memoryId, string? replacement, CancellationToken ct) =>
        throw new NotSupportedException("Use revisioned Edit/Archive. Forget is not available before unified release.");

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
            if (item.ResourceKind != DeletionResourceKind.Memory || item.ResourceId is null)
                throw new InvalidOperationException("Legacy recovery scope is unknown; offline review required.");
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
                memory.Revision++;
                memory.Text = "";
                memory.FactKey = "";
                memory.EvidenceJson = "[]";
                await db.Embeddings.Where(x => x.MemoryId == memory.Id).ExecuteDeleteAsync(ct);
            }

            var revokedIds = revoked.Select(x => x.Id).ToArray();
            await InvalidateMemoryContextAsync(db, item.BotId, revokedIds, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
