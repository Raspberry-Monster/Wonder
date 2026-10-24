using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class NoteSources
{
    public static Task<bool> ValidAsync(WonderDbContext db, string kind, Guid id, CancellationToken ct) =>
        db.NoteDependencies.Where(x => x.TargetKind == kind && x.TargetId == id)
            .AllAsync(x => db.Notes.Any(n => n.Id == x.NoteId && n.Version == x.Version && n.DeletedAt == null &&
                !n.PrivacyDeleted && !n.SourceInvalidated && db.LogicalUsers.Any(u => u.Id == n.UserId && u.AllowNotes)), ct);

    public static async Task InheritAsync(WonderDbContext db, string targetKind, Guid targetId,
        IReadOnlyCollection<Guid> messageIds, Guid? summaryId, CancellationToken ct)
    {
        var sources = await db.NoteDependencies.AsNoTracking().Where(x =>
            (x.TargetKind == "Message" && messageIds.Contains(x.TargetId)) ||
            (summaryId != null && x.TargetKind == "Summary" && x.TargetId == summaryId))
            .Select(x => new { x.NoteId, x.Version }).Distinct().ToArrayAsync(ct);
        foreach (var s in sources)
            await AddAsync(db, targetKind, targetId, s.NoteId, s.Version, ct);
    }

    public static async Task AddAsync(WonderDbContext db, string kind, Guid target, Guid note, long version, CancellationToken ct)
    {
        if (!db.NoteDependencies.Local.Any(x => x.TargetKind == kind && x.TargetId == target && x.NoteId == note && x.Version == version) &&
            !await db.NoteDependencies.AnyAsync(x => x.TargetKind == kind && x.TargetId == target && x.NoteId == note && x.Version == version, ct))
            db.NoteDependencies.Add(new NoteDependency { TargetKind = kind, TargetId = target, NoteId = note, Version = version });
    }

    public static async Task CopyChatAsync(WonderDbContext db, Guid chat, Guid message, CancellationToken ct)
    {
        var sources = await db.NoteDependencies.AsNoTracking().Where(x => x.TargetKind == "Chat" && x.TargetId == chat).ToArrayAsync(ct);
        foreach (var s in sources) await AddAsync(db, "Message", message, s.NoteId, s.Version, ct);
    }

    // Called in the SAME transaction as revision, permission withdrawal, or deletion.
    public static async Task InvalidateAsync(WonderDbContext db, Guid[] noteIds, bool purge, CancellationToken ct)
    {
        var affected = noteIds.ToHashSet();
        while (true)
        {
            var batch = affected.ToArray();
            var derived = await db.NoteDependencies.Where(x => x.TargetKind == "Note" && batch.Contains(x.NoteId))
                .Select(x => x.TargetId).Distinct().ToArrayAsync(ct);
            if (!derived.Any(x => !affected.Contains(x))) break;
            affected.UnionWith(derived);
        }
        var derivedNotes = affected.Except(noteIds).ToArray();
        await db.Notes.Where(x => derivedNotes.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceInvalidated, true), ct);
        noteIds = affected.ToArray();
        var dependencies = await db.NoteDependencies.Where(x => noteIds.Contains(x.NoteId)).ToArrayAsync(ct);
        var messages = dependencies.Where(x => x.TargetKind == "Message").Select(x => x.TargetId).ToArray();
        var summaries = dependencies.Where(x => x.TargetKind == "Summary").Select(x => x.TargetId).ToArray();
        var chats = dependencies.Where(x => x.TargetKind == "Chat").Select(x => x.TargetId).ToArray();
        var operations = db.AgentOperations.Where(x => chats.Contains(x.ChatJobId)).Select(x => x.Id);
        await db.AgentReflectionRequests.Where(x => operations.Contains(x.OperationId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Reason, "[Note source no longer current]"), ct);
        await db.Messages.Where(x => messages.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        await db.Summaries.Where(x => summaries.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        await db.Outbox.Where(x => chats.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ProgressJson, (string?)null), ct);
        var batches = db.Batches.Where(x => chats.Contains(x.SourceJobId)).Select(x => x.Id);
        await db.Parts.Where(x => batches.Contains(x.BatchId) && x.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeliveryStatus.Failed), ct);
        if (!purge) return;
        await db.Messages.Where(x => messages.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "")
            .SetProperty(x => x.SegmentsJson, "[]").SetProperty(x => x.ContextSnapshotJson, (string?)null), ct);
        await db.Summaries.Where(x => summaries.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, ""), ct);
        await db.Parts.Where(x => batches.Contains(x.BatchId)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "")
            .SetProperty(x => x.Status, x => x.Status == DeliveryStatus.Sending ? DeliveryStatus.DeliveryUnknown : x.Status), ct);
    }

    public static async Task PurgeAsync(WonderDbContext db, Guid noteId, CancellationToken ct)
    {
        await InvalidateAsync(db, [noteId], true, ct);
        await db.Notes.Where(x => x.Id == noteId).ExecuteUpdateAsync(s => s.SetProperty(x => x.PrivacyDeleted, true)
            .SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow).SetProperty(x => x.Content, "")
            .SetProperty(x => x.ClaimKind, (string?)null).SetProperty(x => x.SourceMessageIdsJson, "[]")
            .SetProperty(x => x.SourceMemoryIdsJson, "[]"), ct);
        await db.NoteRevisions.Where(x => x.NoteId == noteId).ExecuteDeleteAsync(ct);
    }
}
