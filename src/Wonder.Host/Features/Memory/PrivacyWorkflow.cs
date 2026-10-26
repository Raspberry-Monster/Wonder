using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed partial class PrivacyWorkflow(IDbContextFactory<WonderDbContext> factory, IRecoveryAuthority authority,
    RecoveryPackages packages, MediaStore media)
{
    public static readonly string[] UncoveredLayers = ["IndependentNotesAndRelationships", "ReadOnlyArchive", "HistoricalBackups", "IndependentExports", "ExternalQQ"];

    public async Task<ErasePreviewView> PreviewAsync(Guid actor, Guid memoryId, long revision, Guid[] selectedMessages, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // First discover authorized originals without treating same-user/session records as dependencies.
        var discovery = await PrivacyPlans.BuildAsync(db, actor, memoryId, revision, false, [], ct, media.Root);
        var selection = selectedMessages.Length == 0 ? discovery.SourceMessages.Select(x => x.Id).ToArray() : selectedMessages;
        if (selection.Length == 0) throw new ArgumentException("No retained original evidence is available for Erase; use Forget.");
        var plan = await PrivacyPlans.BuildAsync(db, actor, memoryId, revision, true, selection, ct, media.Root);
        var operation = new MemoryOperation { ActorId = actor, BotId = plan.BotId, Kind = "ErasePreview", Stage = "Previewed",
            IdempotencyKey = "preview:" + Guid.NewGuid().ToString("N"), TargetMemoryId = memoryId, ExpectedRevision = revision,
            PreviewExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), ScopeHash = plan.ScopeHash, TargetsJson = Json.Write(plan.Targets) };
        db.MemoryOperations.Add(operation);
        await db.SaveChangesAsync(ct);
        return new ErasePreviewView(operation.Id, operation.PreviewExpiresAt.Value, plan.ScopeHash, revision,
            plan.SourceMessages.Where(x => selection.Contains(x.Id)).ToArray(), plan.Targets,
            "Erase removes complete selected messages and explicitly traced derived resources. All other facts in each selected message are lost; no fragment erasure is performed.", UncoveredLayers);
    }

    public Task<PrivacyOperationView> ForgetAsync(Guid actor, Guid memoryId, long revision, string key, CancellationToken ct) =>
        StartAsync(actor, memoryId, revision, key, null, null, ct);

    public async Task<PrivacyOperationView> EraseAsync(Guid actor, Guid previewId, string scopeHash, string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var preview = await db.MemoryOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == previewId && x.ActorId == actor &&
            x.Kind == "ErasePreview", ct) ?? throw new KeyNotFoundException();
        return await StartAsync(actor, preview.TargetMemoryId, preview.ExpectedRevision, key, preview, scopeHash, ct);
    }

    private async Task<PrivacyOperationView> StartAsync(Guid actor, Guid memoryId, long revision, string key,
        MemoryOperation? preview, string? scopeHash, CancellationToken ct)
    {
        if (revision < 1 || string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ArgumentException("Revision and idempotency key required.");
        var kind = preview is null ? "Forget" : "Erase";
        var arguments = Json.Hash(Json.Write(new { kind, memoryId, revision, previewId = preview?.Id, scopeHash }));
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.MemoryOperations.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor && x.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.ArgumentsHash != arguments) throw new DbUpdateConcurrencyException("Idempotency key has different arguments.");
            return await GetAsync(actor, existing.Id, ct);
        }
        if (!authority.Configured || !packages.Configured) throw new RecoveryUnavailableException("IndependentRecoveryProtectionNotConfigured");
        if (preview is not null && (preview.PreviewExpiresAt <= DateTimeOffset.UtcNow || preview.ScopeHash != scopeHash))
            throw new DbUpdateConcurrencyException("Erase preview expired or changed.");
        var selected = preview is null ? [] : Json.Read<PrivacyTarget[]>(preview.TargetsJson).Where(x => x.Kind == "Message")
            .Select(x => x.Id).ToArray();
        // Only original authored messages are explicit Erase selections. Derived IDs come from the frozen closure.
        var originalIds = await db.Messages.AsNoTracking().Where(x => selected.Contains(x.Id) && x.SenderId == actor && x.Role == MessageRole.User).Select(x => x.Id).ToArrayAsync(ct);
        var plan = await PrivacyPlans.BuildAsync(db, actor, memoryId, revision, preview is not null, originalIds, ct, media.Root);
        if (preview is not null && plan.ScopeHash != preview.ScopeHash) throw new DbUpdateConcurrencyException("Erase scope changed; preview again.");
        var operationId = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{authority.InstanceId:N}:{actor:N}:{key}"))[..16]);
        var operation = new MemoryOperation { Id = operationId, ActorId = actor, BotId = plan.BotId, IdempotencyKey = key,
            ArgumentsHash = arguments, Kind = kind, Stage = "Preparing", TargetMemoryId = memoryId, ExpectedRevision = revision,
            ScopeHash = plan.ScopeHash, TargetsJson = Json.Write(plan.Targets) };
        var intent = new ProtectionIntent(operation.Id, authority.InstanceId, actor, plan.BotId, kind, plan.ScopeHash, plan.Targets, plan.Suppressions);
        var prepare = await authority.PrepareAsync(intent, ct); // Never hold a PG transaction during external durability.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var user = await db.LogicalUsers.AsNoTracking().SingleAsync(x => x.Id == actor, ct);
            if (!await UserFence.CheckAsync(db, actor, user.RevocationEpoch, ct)) throw new DbUpdateConcurrencyException();
            existing = await db.MemoryOperations.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor && x.IdempotencyKey == key, ct);
            if (existing is not null)
            {
                await tx.RollbackAsync(ct);
                if (existing.Id != operation.Id) await authority.ResolveAsync(intent, false, ct);
                if (existing.ArgumentsHash != arguments) throw new DbUpdateConcurrencyException();
                return await GetAsync(actor, existing.Id, ct);
            }
            var fresh = await PrivacyPlans.BuildAsync(db, actor, memoryId, revision, preview is not null, originalIds, ct, media.Root);
            if (fresh.ScopeHash != plan.ScopeHash) throw new DbUpdateConcurrencyException("Scope changed after protection prepare.");
            var candidateIds = plan.Targets.Where(x => x.Kind == "Candidate").Select(x => x.Id).ToArray();
            var hashes = await db.MemoryCandidates.Where(x => candidateIds.Contains(x.Id)).Select(x => x.CandidateHash).ToArrayAsync(ct);
            operation.AssertionHashesJson = Json.Write(hashes);
            operation.ProtectionReceiptJson = Json.Write(prepare);
            operation.Fenced = true; operation.Stage = "Fenced";
            db.MemoryOperations.Add(operation);
            foreach (var target in plan.Targets)
                db.MemoryOperationItems.Add(new MemoryOperationItem { OperationId = operation.Id, Kind = target.Kind,
                    ResourceId = target.Id, SourceVersion = target.Version });
            foreach (var suppression in plan.Suppressions)
                db.EvidenceSuppressions.Add(new EvidenceSuppression { OperationId = operation.Id, BotId = plan.BotId,
                    SubjectId = actor, MemoryId = suppression.MemoryId, MessageId = suppression.MessageId,
                    MessageVersion = suppression.Version, FactKeyHash = suppression.FactKeyHash, SpansJson = Json.Write(suppression.Spans ?? []) });
            var memoryIds = plan.Targets.Where(x => x.Kind == "Memory").Select(x => x.Id).ToArray();
            var memories = await db.Memories.Where(x => memoryIds.Contains(x.Id)).ToListAsync(ct);
            foreach (var memory in memories)
            {
                if (memory.Status == MemoryStatus.Revoked) continue;
                db.Revocations.Add(new MemoryRevocation { BotId = memory.BotId, UserId = actor, ResourceId = memory.Id,
                    ResourceVersion = memory.Revision, Epoch = user.RevocationEpoch,
                    FactKeyHash = memory.FactKeyHash.Length == 64 ? memory.FactKeyHash : Json.Hash(memory.FactKey) });
                memory.Status = MemoryStatus.Revoked; memory.Text = memory.FactKey = ""; memory.EvidenceJson = "[]"; memory.Revision++;
            }
            await db.Embeddings.Where(x => memoryIds.Contains(x.MemoryId)).ExecuteDeleteAsync(ct);
            await MemoryService.InvalidateMemoryContextAsync(db, plan.BotId, memoryIds, ct);
            if (preview is not null) await FenceErasedSourcesAsync(db, plan.Targets, ct);
            if (preview is not null) await RelationshipClaims.InvalidateAsync(db, plan.BotId, [], originalIds, ct);
            Jobs.Add(db, plan.BotId, JobKind.MemoryPrivacy, new IdPayload(operation.Id));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // An ambiguous DB failure is not an Abort. Only a proven rollback scope is eligible for explicit resolution.
            if (!await db.MemoryOperations.AsNoTracking().AnyAsync(x => x.Id == operation.Id && x.Fenced, ct))
                await authority.ResolveAsync(intent, false, ct);
            throw;
        }
        try
        {
            var committed = await authority.ResolveAsync(intent, true, ct);
            await db.MemoryOperations.Where(x => x.Id == operation.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ProtectionReceiptJson, Json.Write(committed)).SetProperty(x => x.Stage, "PendingCleanup"), ct);
            // One bounded pass implements synchronous-first completion. Durable Outbox continues after interruption.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            await ProcessAsync(operation.Id, budget.Token);
        }
        catch (Exception ex) when (ex is RecoveryUnavailableException or HttpRequestException or IOException or OperationCanceledException)
        {
            await db.MemoryOperations.Where(x => x.Id == operation.Id && x.Stage != "Completed").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ErrorCode, "ProtectionOrCleanupPending").SetProperty(x => x.Revision, x => x.Revision + 1), CancellationToken.None);
        }
        return await GetAsync(actor, operation.Id, CancellationToken.None);
    }

    public async Task<PrivacyOperationView> GetAsync(Guid actor, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var operation = await db.MemoryOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ActorId == actor &&
            (x.Kind == "Forget" || x.Kind == "Erase"), ct) ?? throw new KeyNotFoundException();
        var items = await db.MemoryOperationItems.AsNoTracking().Where(x => x.OperationId == id).OrderBy(x => x.Kind).ThenBy(x => x.Id)
            .Select(x => new PrivacyItemView(x.Id, x.Kind, x.ResourceId, x.State, x.ErrorCode)).ToArrayAsync(ct);
        return new PrivacyOperationView(id, operation.Kind, operation.Stage, operation.Revision, operation.Fenced,
            operation.CleanupVerified, operation.RecoveryVerified, operation.Stage == "Completed" && operation.CleanupVerified && operation.RecoveryVerified,
            items, UncoveredLayers, operation.ErrorCode, operation.TargetMemoryId,
            await db.Memories.Where(x => x.Id == operation.TargetMemoryId && x.SubjectId == actor).Select(x => x.Revision).SingleOrDefaultAsync(ct));
    }

    public async Task RunAsync(Guid jobId, CancellationToken ct, Guid? lease = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId && x.Type == JobKind.MemoryPrivacy, ct);
        Jobs.RequireLease(job, lease);
        if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return;
        await ProcessAsync(Json.Read<IdPayload>(job.Payload).Id, ct, jobId, lease);
        await db.Entry(job).ReloadAsync(ct);
        Jobs.RequireLease(job, lease);
        if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return;
        var operation = await db.MemoryOperations.AsNoTracking().SingleAsync(x => x.Id == Json.Read<IdPayload>(job.Payload).Id, ct);
        if (operation.Stage is "Completed" or "NeedsReview") Jobs.Complete(db, job);
        else Jobs.Continue<object?>(job, null, DateTimeOffset.UtcNow.AddSeconds(30));
        await Jobs.SaveAsync(db, job, lease, ct);
    }

    public async Task RetryAsync(Guid actor, Guid operationId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var operation = await db.MemoryOperations.SingleOrDefaultAsync(x => x.Id == operationId && x.ActorId == actor && x.Fenced, ct)
                        ?? throw new KeyNotFoundException();
        if (operation.Stage == "Completed") return;
        operation.Stage = "PendingCleanup"; operation.ErrorCode = null; operation.Revision++;
        var payload = Json.Write(new IdPayload(operationId));
        if (!await db.Outbox.AnyAsync(x => x.Type == JobKind.MemoryPrivacy && x.Payload == payload &&
            (x.Status == JobStatus.Pending || x.Status == JobStatus.Running), ct)) Jobs.Add(db, operation.BotId, JobKind.MemoryPrivacy, new IdPayload(operationId));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private static async Task FenceErasedSourcesAsync(WonderDbContext db, PrivacyTarget[] targets, CancellationToken ct)
    {
        var messages = targets.Where(x => x.Kind == "Message").Select(x => x.Id).ToArray();
        var summaries = targets.Where(x => x.Kind == "Summary").Select(x => x.Id).ToArray();
        await db.Messages.Where(x => messages.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        await db.Summaries.Where(x => summaries.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true)
            .SetProperty(x => x.SafeProjectionJson, (string?)null).SetProperty(x => x.ProjectionState, "Invalid")
            .SetProperty(x => x.ProjectionRevision, x => x.ProjectionRevision + 1), ct);
    }
}
