using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public interface IRecoveryStartup
{
    Task<RecoveryProof> VerifyAsync(CancellationToken ct);
}

public sealed class RecoveryBootstrap(IDbContextFactory<WonderDbContext> factory, IRecoveryAuthority authority,
    RecoveryPackages packages, IConfiguration configuration) : IRecoveryStartup
{
    public async Task<RecoveryProof> VerifyAsync(CancellationToken ct)
    {
        if (!authority.Configured || !packages.Configured) throw new RecoveryUnavailableException("IndependentRecoveryProtectionNotConfigured");
        await using var db = await factory.CreateDbContextAsync(ct);
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any()) throw new RecoveryUnavailableException("RecoverySchemaRequiresOfflineMigration");
        if (await db.Memories.AnyAsync(x => x.Status == MemoryStatus.Revoked && (x.Text != "" || x.FactKey != "" || x.EvidenceJson != "[]"), ct))
            throw new RecoveryUnavailableException("RevokedMemoryPayloadRemains");
        var operations = await db.MemoryOperations.AsNoTracking().Where(x => x.Fenced && (x.Kind == "Forget" || x.Kind == "Erase"))
            .OrderBy(x => x.Id).ToListAsync(ct);
        var intents = new List<ProtectionIntent>();
        foreach (var operation in operations)
        {
            var intent = await packages.IntentAsync(db, operation, authority.InstanceId, ct);
            var items = await db.MemoryOperationItems.AsNoTracking().Where(x => x.OperationId == operation.Id)
                .Select(x => new RecoveryItem(x.Kind, x.ResourceId, x.SourceVersion, x.State)).ToArrayAsync(ct);
            RecoveryPackages.ValidateOperation(new RecoveryOperation(intent, Json.Read<string[]>(operation.AssertionHashesJson), items,
                operation.IdempotencyKey, operation.ArgumentsHash, operation.TargetMemoryId, operation.ExpectedRevision), authority.InstanceId);
            intents.Add(intent);
            var targets = Json.Read<PrivacyTarget[]>(operation.TargetsJson);
            var memories = targets.Where(x => x.Kind == "Memory").Select(x => x.Id).ToArray();
            if (await db.Memories.AnyAsync(x => memories.Contains(x.Id) && x.Status != MemoryStatus.Revoked, ct))
                throw new RecoveryUnavailableException("RecoveryMemoryFenceMissing");
            if (operation.Kind == "Erase")
            {
                var messages = targets.Where(x => x.Kind == "Message").Select(x => x.Id).ToArray();
                if (await db.Messages.AnyAsync(x => messages.Contains(x.Id) && !x.ContextInvalidated, ct))
                    throw new RecoveryUnavailableException("RecoveryMessageFenceMissing");
                foreach (var target in targets.Where(x => x.Kind == "Summary"))
                    if (await db.Summaries.AnyAsync(x => x.Id == target.Id && x.Revision.ToString() == target.Version && !x.ContextInvalidated, ct))
                        throw new RecoveryUnavailableException("RecoverySummaryFenceMissing");
            }
            if (operation.Stage == "Completed" && (!operation.RecoveryVerified || !operation.CleanupVerified ||
                await db.MemoryOperationItems.AnyAsync(x => x.OperationId == operation.Id && x.State != "Completed", ct)))
                throw new RecoveryUnavailableException("RecoveryCompletionInvariantFailed");
            if (operation.Stage == "Completed") await VerifyCompletedPayloadsAsync(db, operation, targets, configuration, ct);
        }
        var schema = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "Unknown";
        var legacy = await db.Revocations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct);
        if (legacy.Any(x => x.ResourceKind == DeletionResourceKind.MemoryContext || x.ResourceId is null))
            throw new RecoveryUnavailableException("LegacyRecoveryScopeUnknown");
        foreach (var entry in legacy)
        {
            if (entry.ResourceKind == DeletionResourceKind.Memory && await db.Memories.AnyAsync(x => x.Id == entry.ResourceId &&
                    (x.BotId != entry.BotId || x.SubjectId != entry.UserId || x.Status != MemoryStatus.Revoked), ct) ||
                entry.ResourceKind == DeletionResourceKind.Note && await db.Notes.AnyAsync(x => x.Id == entry.ResourceId &&
                    (x.BotId != entry.BotId || x.UserId != entry.UserId || !x.PrivacyDeleted || x.Content != ""), ct))
                throw new RecoveryUnavailableException("RecoveryLegacyFenceMissing");
        }
        return await authority.VerifyAsync(new RecoveryCoverage(authority.InstanceId, schema, RecoveryPackages.CoverageHash(intents),
            configuration["Recovery:TrustedBaseline"] ?? "", Json.Hash(Json.Write(legacy))), ct);
    }

    private static async Task VerifyCompletedPayloadsAsync(WonderDbContext db, MemoryOperation operation, PrivacyTarget[] targets,
        IConfiguration configuration, CancellationToken ct)
    {
        var hashes = Json.Read<string[]>(operation.AssertionHashesJson).ToHashSet(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var remains = target.Kind switch
            {
                "Memory" => await db.Embeddings.AnyAsync(x => x.MemoryId == target.Id, ct),
                "Candidate" => await db.MemoryCandidates.AnyAsync(x => x.Id == target.Id &&
                    (x.Text != "" || x.FactKey != "" || x.EvidenceJson != "[]" || x.AssertionsJson != null || x.Reason != ""), ct),
                "Message" => await db.Messages.AnyAsync(x => x.Id == target.Id && (x.Text != "" || x.SegmentsJson != "[]"), ct),
                "OutgoingPart" => await db.Parts.AnyAsync(x => x.Id == target.Id && x.Text != "", ct),
                "SummaryDraft" => await db.Outbox.AnyAsync(x => x.Id == target.Id && x.ProgressJson != null, ct),
                "Media" => await db.Media.AnyAsync(x => x.Id == target.Id, ct),
                _ => false
            };
            if (target.Kind == "Media")
            {
                if (string.IsNullOrWhiteSpace(target.MediaLocator) || Path.IsPathRooted(target.MediaLocator)) remains = true;
                else
                {
                    var root = Path.GetFullPath(configuration["Storage:MediaPath"] ?? "data/media");
                    var path = Path.GetFullPath(Path.Combine(root, target.MediaLocator));
                    if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || File.Exists(path)) remains = true;
                }
            }
            if (target.Kind is "ReflectionMemory" or "ReflectionDraft")
            {
                var json = target.Kind == "ReflectionMemory" ? await db.Reflections.Where(x => x.Id == target.Id).Select(x => x.Payload).SingleOrDefaultAsync(ct)
                    : await db.Outbox.Where(x => x.Id == target.Id).Select(x => x.ProgressJson).SingleOrDefaultAsync(ct);
                if (json is not null)
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(json);
                    var array = (target.Kind == "ReflectionMemory" ? node?["memories"] : node?["proposal"]?["memories"]) as System.Text.Json.Nodes.JsonArray;
                    remains = array is null || array.Any(x => x is not null && MemoryAssertionPayload.Matches(x, hashes)) ||
                        !await MemoryAssertionPayload.RemainingSafeAsync(db, operation.BotId, operation.ActorId, array, ct);
                }
            }
            if (target.Kind is "Summary" or "SummaryProjection")
            {
                var summary = await db.Summaries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target.Id, ct);
                if (summary is not null && (target.Kind == "Summary" ? summary.Text != "" : summary.SafeProjectionJson is not null))
                {
                    var source = target.Kind == "Summary" ? ContentSources.Summary(summary) : ContentSources.Projection(summary);
                    remains = summary.BotId != operation.BotId || source.Version == target.Version ||
                        !await ContentSources.ValidAsync(db, operation.BotId, [source], ct, operation.ActorId);
                }
            }
            if (remains) throw new RecoveryUnavailableException("RecoveryCompletedPayloadRemains");
        }
    }
}

public sealed class RecoveryReplay(IDbContextFactory<WonderDbContext> factory, IRecoveryAuthority authority,
    RecoveryPackages packages, IConfiguration configuration)
{
    public async Task ReplayAsync(RecoveryArchive archive, CancellationToken ct)
    {
        var snapshot = packages.Decode(archive, authority.InstanceId);
        if (snapshot.Legacy.Any(x => x.ResourceKind == DeletionResourceKind.MemoryContext || x.ResourceId is null))
            throw new RecoveryUnavailableException("LegacyRecoveryScopeUnknown");
        var proof = await authority.VerifyAsync(new RecoveryCoverage(snapshot.InstanceId, snapshot.Schema,
            RecoveryPackages.CoverageHash(snapshot.Operations.Select(x => x.Intent)), configuration["Recovery:TrustedBaseline"] ?? "",
            Json.Hash(Json.Write(snapshot.Legacy.OrderBy(x => x.Id)))), ct);
        if (!proof.Passed) throw new RecoveryUnavailableException("IndependentRecoveryCoverageUnverified");
        await using var db = await factory.CreateDbContextAsync(ct);
        var schema = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
        if (schema != snapshot.Schema) throw new RecoveryUnavailableException("RecoverySchemaMismatch");
        foreach (var record in snapshot.Operations.OrderBy(x => x.Intent.ActorId).ThenBy(x => x.Intent.OperationId))
        {
            var intent = record.Intent;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var user = await db.LogicalUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == intent.ActorId, ct);
            if (user is null || !await db.Bots.AnyAsync(x => x.Id == intent.BotId, ct))
                throw new RecoveryUnavailableException("RecoveryScopeIdentityMissing");
            if (!await UserFence.CheckAsync(db, user.Id, user.RevocationEpoch, ct)) throw new DbUpdateConcurrencyException();
            var operation = await db.MemoryOperations.SingleOrDefaultAsync(x => x.Id == intent.OperationId, ct);
            if (operation is not null && (operation.ActorId != intent.ActorId || operation.ScopeHash != intent.ScopeHash || operation.BotId != intent.BotId))
                throw new RecoveryUnavailableException("RecoveryOperationScopeMismatch");
            if (operation is null)
            {
                operation = new MemoryOperation { Id = intent.OperationId, ActorId = intent.ActorId, BotId = intent.BotId,
                    IdempotencyKey = record.IdempotencyKey, ArgumentsHash = record.ArgumentsHash,
                    TargetMemoryId = record.TargetMemoryId, ExpectedRevision = record.ExpectedRevision, Kind = intent.Kind, ScopeHash = intent.ScopeHash,
                    TargetsJson = Json.Write(intent.Targets), AssertionHashesJson = Json.Write(record.AssertionHashes) };
                db.MemoryOperations.Add(operation);
            }
            operation.Fenced = true; operation.CleanupVerified = false; operation.RecoveryVerified = false;
            if (operation.Stage != "Fenced") operation.Revision++;
            operation.Stage = "Fenced";
            foreach (var target in intent.Targets)
            {
                var item = await db.MemoryOperationItems.SingleOrDefaultAsync(x => x.OperationId == operation.Id && x.Kind == target.Kind && x.ResourceId == target.Id, ct);
                if (item is null) db.MemoryOperationItems.Add(new MemoryOperationItem { OperationId = operation.Id, Kind = target.Kind,
                    ResourceId = target.Id, SourceVersion = target.Version });
                else if (item.State != "Pending") { item.State = "Pending"; item.VerifiedAt = null; item.Revision++; }
                if (target.Kind == "Memory")
                {
                    var memory = await db.Memories.SingleOrDefaultAsync(x => x.Id == target.Id, ct);
                    if (memory is null) continue;
                    if (memory.BotId != intent.BotId || memory.SubjectId != intent.ActorId ||
                        memory.Status != MemoryStatus.Revoked && memory.Revision.ToString() != target.Version)
                        throw new RecoveryUnavailableException("RecoveryResourceVersionMismatch");
                    if (memory.Status != MemoryStatus.Revoked || memory.Text != "" || memory.FactKey != "" || memory.EvidenceJson != "[]") memory.Revision++;
                    memory.Status = MemoryStatus.Revoked; memory.Text = memory.FactKey = ""; memory.EvidenceJson = "[]";
                    await db.Embeddings.Where(x => x.MemoryId == target.Id).ExecuteDeleteAsync(ct);
                }
                else if (target.Kind == "Message" && intent.Kind == "Erase")
                    await db.Messages.Where(x => x.Id == target.Id && x.BotId == intent.BotId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
                else if (target.Kind == "Summary" && intent.Kind == "Erase")
                    await db.Summaries.Where(x => x.Id == target.Id && x.BotId == intent.BotId).ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.ContextInvalidated, true).SetProperty(x => x.SafeProjectionJson, (string?)null)
                        .SetProperty(x => x.ProjectionState, "Invalid").SetProperty(x => x.ProjectionRevision, x => x.ProjectionRevision + 1), ct);
            }
            foreach (var source in intent.Suppressions)
                if (!await db.EvidenceSuppressions.AnyAsync(x => x.OperationId == intent.OperationId && x.MemoryId == source.MemoryId && x.MessageId == source.MessageId, ct))
                    db.EvidenceSuppressions.Add(new EvidenceSuppression { OperationId = intent.OperationId, BotId = intent.BotId, SubjectId = intent.ActorId,
                        MemoryId = source.MemoryId, MessageId = source.MessageId, MessageVersion = source.Version, FactKeyHash = source.FactKeyHash,
                        SpansJson = Json.Write(source.Spans ?? []) });
            var memories = intent.Targets.Where(x => x.Kind == "Memory").Select(x => x.Id).ToArray();
            await MemoryService.InvalidateMemoryContextAsync(db, intent.BotId, memories, ct);
            var originalMessages = intent.Kind == "Erase" ? intent.Targets.Where(x => x.Kind == "Message").Select(x => x.Id).ToArray() : [];
            if (originalMessages.Length > 0) await RelationshipClaims.InvalidateAsync(db, intent.BotId, [], originalMessages, ct);
            var payload = Json.Write(new IdPayload(operation.Id));
            if (!await db.Outbox.AnyAsync(x => x.Type == JobKind.MemoryPrivacy && x.Payload == payload &&
                (x.Status == JobStatus.Pending || x.Status == JobStatus.Running), ct)) Jobs.Add(db, intent.BotId, JobKind.MemoryPrivacy, new IdPayload(operation.Id));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); db.ChangeTracker.Clear();
        }
        // Replay only concrete legacy IDs. Hash-only/shared-context records require offline scope review.
        foreach (var legacy in snapshot.Legacy)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var user = await db.LogicalUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == legacy.UserId, ct);
            if (user is null || !await db.Bots.AnyAsync(x => x.Id == legacy.BotId, ct))
                throw new RecoveryUnavailableException("RecoveryScopeIdentityMissing");
            if (!await UserFence.CheckAsync(db, user.Id, user.RevocationEpoch, ct)) throw new DbUpdateConcurrencyException();
            if (legacy.ResourceKind == DeletionResourceKind.Note)
            {
                var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == legacy.ResourceId, ct);
                if (note is not null && (note.BotId != legacy.BotId || note.UserId != legacy.UserId))
                    throw new RecoveryUnavailableException("RecoveryResourceVersionMismatch");
                await NoteSources.PurgeAsync(db, legacy.ResourceId!.Value, ct);
            }
            else
            {
                var memory = await db.Memories.SingleOrDefaultAsync(x => x.Id == legacy.ResourceId, ct);
                if (memory is not null && (memory.BotId != legacy.BotId || memory.SubjectId != legacy.UserId))
                    throw new RecoveryUnavailableException("RecoveryResourceVersionMismatch");
                if (memory is not null && memory.Status != MemoryStatus.Revoked)
                {
                    if (memory.BotId != legacy.BotId || memory.SubjectId != legacy.UserId || legacy.ResourceVersion is null ||
                        memory.Revision != legacy.ResourceVersion || Json.Hash(memory.FactKey) != legacy.FactKeyHash)
                        throw new RecoveryUnavailableException("RecoveryResourceVersionMismatch");
                    memory.Status = MemoryStatus.Revoked; memory.Text = memory.FactKey = ""; memory.EvidenceJson = "[]"; memory.Revision++;
                }
                await db.Embeddings.Where(x => x.MemoryId == legacy.ResourceId).ExecuteDeleteAsync(ct);
                await MemoryService.InvalidateMemoryContextAsync(db, legacy.BotId, [legacy.ResourceId!.Value], ct);
            }
            if (!await db.Revocations.AnyAsync(x => x.Id == legacy.Id, ct)) db.Revocations.Add(legacy);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); db.ChangeTracker.Clear();
        }
    }
}
