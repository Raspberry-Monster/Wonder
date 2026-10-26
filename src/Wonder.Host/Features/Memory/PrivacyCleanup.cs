using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed partial class PrivacyWorkflow
{
    private async Task ProcessAsync(Guid operationId, CancellationToken ct, Guid? jobId = null, Guid? lease = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var operation = await db.MemoryOperations.AsNoTracking().SingleAsync(x => x.Id == operationId && x.Fenced, ct);
        if (operation.Stage == "Completed") return;
        // Prepare/Commit hashes are canonical: order suppressions exactly as recorded at initial admission.
        var intent = await packages.IntentAsync(db, operation, authority.InstanceId, ct);
        if (!operation.RecoveryVerified)
        {
            await authority.ResolveAsync(intent, true, ct);
            var archive = await packages.CreateAsync(db, operation.ActorId, authority.InstanceId, ct);
            var receipt = await authority.ProtectAsync(intent, archive, ct);
            if (receipt.OperationId != operationId || receipt.IntentHash != Json.Hash(Json.Write(intent)) || receipt.Phase != "Protect" ||
                receipt.ArchiveDigest != archive.Digest || receipt.ManifestDigest != Json.Hash(archive.ManifestJson))
                throw new RecoveryUnavailableException("RecoveryProtectionScopeMismatch");
            await db.MemoryOperations.Where(x => x.Id == operationId && x.Fenced && !x.RecoveryVerified)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RecoveryVerified, true)
                    .SetProperty(x => x.ProtectionReceiptJson, Json.Write(receipt)).SetProperty(x => x.Stage, "Deleting")
                    .SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        }
        var ids = await db.MemoryOperationItems.Where(x => x.OperationId == operationId && x.State != "Completed")
            .OrderBy(x => x.Kind).ThenBy(x => x.Id).Select(x => x.Id).Take(256).ToArrayAsync(ct);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var epoch = await db.LogicalUsers.Where(x => x.Id == operation.ActorId).Select(x => x.RevocationEpoch).SingleAsync(ct);
            if (!await UserFence.CheckAsync(db, operation.ActorId, epoch, ct)) throw new DbUpdateConcurrencyException();
            if (jobId is { } leasedJob && lease is { } expectedLease)
            {
                var now = await InteractionBoundary.DatabaseNowAsync(db, DateTimeOffset.UtcNow, ct);
                if (await db.Outbox.Where(x => x.Id == leasedJob && x.LeaseId == expectedLease && x.Status == JobStatus.Running && x.LeaseUntil > now)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, expectedLease), ct) != 1)
                    throw new DbUpdateConcurrencyException("Privacy cleanup lease changed.");
            }
            var item = await db.MemoryOperationItems.SingleAsync(x => x.Id == id, ct);
            await db.Entry(item).ReloadAsync(ct);
            if (item.State == "Completed") { await tx.CommitAsync(ct); continue; }
            if (item.Kind == "Media")
            {
                // File I/O runs after committing a durable claimed item, outside a PG transaction.
                item.State = "Processing"; item.Revision++;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                await CleanMediaAsync(db, operation, item, ct);
                continue;
            }
            try
            {
                var verified = await CleanDatabaseItemAsync(db, operation, item, ct);
                item.State = verified ? "Completed" : "NeedsReview";
                item.ErrorCode = verified ? null : "ExactPayloadScopeUnprovable";
                item.VerifiedAt = verified ? DateTimeOffset.UtcNow : null;
                item.Revision++;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            catch (JsonException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                await db.MemoryOperationItems.Where(x => x.Id == id && x.State != "Completed").ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.State, "NeedsReview").SetProperty(x => x.ErrorCode, "CorruptStructuredPayload")
                    .SetProperty(x => x.Revision, x => x.Revision + 1), ct);
            }
        }
        db.ChangeTracker.Clear();
        await using var finish = await db.Database.BeginTransactionAsync(ct);
        var current = await db.MemoryOperations.SingleAsync(x => x.Id == operationId, ct);
        var complete = await db.MemoryOperationItems.Where(x => x.OperationId == operationId).AllAsync(x => x.State == "Completed", ct);
        var review = await db.MemoryOperationItems.AnyAsync(x => x.OperationId == operationId && x.State == "NeedsReview", ct);
        current.CleanupVerified = complete;
        current.Stage = complete && current.RecoveryVerified ? "Completed" : review ? "NeedsReview" : "PendingCleanup";
        current.ErrorCode = review ? "ItemsRequireReview" : null;
        current.Revision++;
        await db.SaveChangesAsync(ct); await finish.CommitAsync(ct);
    }

    private static async Task<bool> CleanDatabaseItemAsync(WonderDbContext db, MemoryOperation operation, MemoryOperationItem item, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case "Memory":
                if (!await db.Memories.AnyAsync(x => x.Id == item.ResourceId, ct))
                    return !await db.Embeddings.AnyAsync(x => x.MemoryId == item.ResourceId, ct);
                return await db.Memories.AnyAsync(x => x.Id == item.ResourceId && x.SubjectId == operation.ActorId && x.BotId == operation.BotId &&
                    x.Status == MemoryStatus.Revoked && x.Text == "" && x.FactKey == "" && x.EvidenceJson == "[]" &&
                    !db.Embeddings.Any(e => e.MemoryId == x.Id), ct);
            case "Candidate":
                await db.MemoryCandidates.Where(x => x.Id == item.ResourceId && x.BotId == operation.BotId && x.SubjectId == operation.ActorId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "").SetProperty(x => x.FactKey, "").SetProperty(x => x.EvidenceJson, "[]")
                        .SetProperty(x => x.AssertionsJson, (string?)null).SetProperty(x => x.Reason, "").SetProperty(x => x.Status, MemoryMaintenanceStatus.Cancelled), ct);
                return !await db.MemoryCandidates.AnyAsync(x => x.Id == item.ResourceId && (x.Text != "" || x.Reason != "" || x.AssertionsJson != null || x.EvidenceJson != "[]"), ct);
            case "ReflectionMemory":
                var reflection = await db.Reflections.SingleOrDefaultAsync(x => x.Id == item.ResourceId && x.BotId == operation.BotId && x.SubjectId == operation.ActorId, ct);
                if (reflection is null) return true;
                var node = JsonNode.Parse(reflection.Payload) as JsonObject ?? throw new JsonException();
                if (node["memories"] is not JsonArray array) return reflection.Payload == "{}";
                var hashes = Json.Read<string[]>(operation.AssertionHashesJson).ToHashSet(StringComparer.Ordinal);
                if (hashes.Count == 0 && array.Count > 0) { reflection.MemorySourcesBlocked = true; return false; }
                RemoveAssertions(array, hashes);
                reflection.Payload = node.ToJsonString(Json.Options);
                var safe = await MemoryAssertionPayload.RemainingSafeAsync(db, operation.BotId, operation.ActorId, array, ct);
                if (!safe) reflection.MemorySourcesBlocked = true;
                return safe;
            case "ReflectionDraft":
                var draft = await db.Outbox.SingleOrDefaultAsync(x => x.Id == item.ResourceId && x.BotId == operation.BotId, ct);
                if (draft?.ProgressJson is null) return true;
                var progress = JsonNode.Parse(draft.ProgressJson) as JsonObject ?? throw new JsonException();
                if (progress["proposal"]?["memories"] is not JsonArray memories) return false;
                RemoveAssertions(memories, Json.Read<string[]>(operation.AssertionHashesJson).ToHashSet(StringComparer.Ordinal));
                draft.ProgressJson = progress.ToJsonString(Json.Options);
                return await MemoryAssertionPayload.RemainingSafeAsync(db, operation.BotId, operation.ActorId, memories, ct);
            case "SummaryDraft":
                await db.Outbox.Where(x => x.Id == item.ResourceId && x.BotId == operation.BotId && x.Type == JobKind.Summary)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProgressJson, (string?)null), ct);
                return !await db.Outbox.AnyAsync(x => x.Id == item.ResourceId && x.ProgressJson != null, ct);
            case "SummaryProjection":
                var projected = await db.Summaries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.ResourceId && x.BotId == operation.BotId, ct);
                if (projected?.SafeProjectionJson is null) return true;
                // A newer independent projection is outside the deleted version; validate its roots rather than delete it.
                if (projected.ProjectionRevision.ToString() != item.SourceVersion)
                    return await ContentSources.ValidAsync(db, operation.BotId, [ContentSources.Projection(projected)], ct, operation.ActorId);
                await db.Summaries.Where(x => x.Id == item.ResourceId && x.ProjectionRevision == projected.ProjectionRevision)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SafeProjectionJson, (string?)null).SetProperty(x => x.ProjectionState, "Invalid")
                        .SetProperty(x => x.ProjectionRevision, x => x.ProjectionRevision + 1), ct);
                return true;
            case "Message" when operation.Kind == "Erase":
                var message = await db.Messages.SingleOrDefaultAsync(x => x.Id == item.ResourceId && x.BotId == operation.BotId, ct);
                if (message is null || message.Text == "" && message.SegmentsJson == "[]") return true;
                if (!message.ContextInvalidated || ContentSources.Message(message).Version != item.SourceVersion) return false;
                message.Text = ""; message.SegmentsJson = "[]"; message.ContextSnapshotJson = null; message.AllowReflection = false;
                return true;
            case "Summary" when operation.Kind == "Erase":
                var summary = await db.Summaries.SingleOrDefaultAsync(x => x.Id == item.ResourceId && x.BotId == operation.BotId, ct);
                if (summary is null || summary.Text == "") return true;
                if (summary.Revision.ToString() != item.SourceVersion)
                    return await ContentSources.ValidAsync(db, operation.BotId, [ContentSources.Summary(summary)], ct, operation.ActorId);
                if (!summary.ContextInvalidated) return false;
                summary.Text = ""; summary.SafeProjectionJson = null; summary.ProjectionState = "Invalid"; summary.ProjectionRevision++; summary.Revision++;
                return true;
            case "OutgoingPart" when operation.Kind == "Erase":
                var part = await db.Parts.SingleOrDefaultAsync(x => x.Id == item.ResourceId, ct);
                if (part is null || part.Text == "") return true;
                if (Json.Hash(part.Text) != item.SourceVersion) return false;
                part.Text = "";
                if (part.Status == DeliveryStatus.Pending) { part.Status = DeliveryStatus.Failed; part.FailureReason = "ErasedSource"; }
                // In-flight, sent and unknown deliveries are not falsely relabelled as recalled.
                return true;
            default: return false;
        }
    }

    private static void RemoveAssertions(JsonArray array, IReadOnlySet<string> hashes)
    {
        for (var index = array.Count - 1; index >= 0; index--)
            if (array[index] is { } assertion && MemoryAssertionPayload.Matches(assertion, hashes)) array.RemoveAt(index);
    }

    private async Task CleanMediaAsync(WonderDbContext db, MemoryOperation operation, MemoryOperationItem item, CancellationToken ct)
    {
        try
        {
            var asset = await db.Media.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.ResourceId, ct);
            var target = Json.Read<PrivacyTarget[]>(operation.TargetsJson).Single(x => x.Kind == "Media" && x.Id == item.ResourceId);
            if (string.IsNullOrWhiteSpace(target.MediaLocator) || target.ContentHash?.Length != 64 || Path.IsPathRooted(target.MediaLocator))
            { await SetMediaResultAsync(db, item.Id, "NeedsReview", "MediaRecoveryLocatorOrContentHashUnknown", ct); return; }
            var path = Path.GetFullPath(Path.Combine(media.Root, target.MediaLocator));
            var root = Path.TrimEndingDirectorySeparator(media.Root) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                asset is not null && (asset.BotId != operation.BotId || Path.GetFullPath(asset.Path) != path || Json.Hash(Json.Write(new { asset.MessageId, asset.Path, asset.Hash })) != item.SourceVersion) ||
                await db.Media.AnyAsync(x => x.Id != item.ResourceId && x.Path == path, ct))
            { await SetMediaResultAsync(db, item.Id, "NeedsReview", "MediaOwnershipUnprovenOrShared", ct); return; }
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            {
                if (parent.Exists && (parent.LinkTarget is not null || (parent.Attributes & FileAttributes.ReparsePoint) != 0))
                { await SetMediaResultAsync(db, item.Id, "NeedsReview", "MediaLinkRequiresReview", ct); return; }
            }
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                { await SetMediaResultAsync(db, item.Id, "NeedsReview", "MediaLinkRequiresReview", ct); return; }
                // Hash means file content here; MediaAsset.Hash is the URL cache key and is not content evidence.
                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    if (Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, ct)) != target.ContentHash)
                    { await SetMediaResultAsync(db, item.Id, "NeedsReview", "MediaContentChanged", ct); return; }
                File.Delete(path);
            }
            if (File.Exists(path)) throw new IOException();
            await db.Media.Where(x => x.Id == item.ResourceId).ExecuteDeleteAsync(ct);
            await SetMediaResultAsync(db, item.Id, "Completed", null, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { await SetMediaResultAsync(db, item.Id, "Failed", "MediaCleanupFailed", ct); }
    }

    private static Task<int> SetMediaResultAsync(WonderDbContext db, Guid id, string state, string? code, CancellationToken ct) =>
        db.MemoryOperationItems.Where(x => x.Id == id && x.State != "Completed").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state)
            .SetProperty(x => x.ErrorCode, code).SetProperty(x => x.VerifiedAt, state == "Completed" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)
            .SetProperty(x => x.Revision, x => x.Revision + 1), ct);
}
