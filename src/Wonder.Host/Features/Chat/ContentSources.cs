using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class ContentSources
{
    public static async Task<ContentSource[]> SummaryRootsAsync(WonderDbContext db, ConversationSummary row,
        CancellationToken ct)
    {
        if (ProjectionReady(row)) return Json.Read<SafeSummaryProjection>(row.SafeProjectionJson!).Sources.ToArray();
        if (TryRead(row.SourcesJson, out var refs)) return refs;
        if (row.SourcesJson is not null) throw new InvalidOperationException("Corrupt summary provenance.");
        if (!ConversationHistory.TryMemoryIds(row.MemoryIdsJson, false, out var memoryIds) ||
            !ConversationHistory.TryMemoryIds(row.MessageIdsJson, false, out var messageIds))
            throw new InvalidOperationException("Unknown summary roots.");
        var facts = await db.Memories.AsNoTracking().Where(x => memoryIds.Contains(x.Id) && x.BotId == row.BotId)
            .ToListAsync(ct);
        var messages = await db.Messages.AsNoTracking().Where(x => messageIds.Contains(x.Id) && x.BotId == row.BotId)
            .ToListAsync(ct);
        if (facts.Count != memoryIds.Distinct().Count() || messages.Count != messageIds.Distinct().Count())
            throw new InvalidOperationException("Missing summary roots.");
        return facts.Select(Memory).Concat(messages.Select(Message)).ToArray();
    }

    public static ContentSource Memory(Memory row)
    {
        return new ContentSource("Memory", row.Id, Version(row.Revision));
    }

    public static ContentSource Message(ChatMessage row)
    {
        return new ContentSource("Message", row.Id,
            Json.Hash(Json.Write(new { row.Text, row.SegmentsJson, row.ContextSnapshotJson })));
    }

    public static ContentSource Summary(ConversationSummary row)
    {
        return new ContentSource("Summary", row.Id, Version(row.Revision));
    }

    public static ContentSource Projection(ConversationSummary row)
    {
        return new ContentSource("SummaryProjection", row.Id, Version(row.ProjectionRevision));
    }

    private static string Version(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryRead(string? json, out ContentSource[] sources)
    {
        sources = [];
        if (json is null) return false;
        try
        {
            sources = Json.Read<ContentSource[]>(json);
            return sources is not null && sources.All(x => x is not null && x.Id != Guid.Empty &&
                                                           !string.IsNullOrWhiteSpace(x.Version));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task<bool> ValidAsync(WonderDbContext db, Guid bot, IEnumerable<ContentSource> sources,
        CancellationToken ct, Guid? subject = null, DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        var seen = new HashSet<ContentSource>();
        var path = new HashSet<ContentSource>();

        async Task<bool> Visit(ContentSource source, int depth)
        {
            if (depth > 16 || seen.Count > 512 || source is null || path.Contains(source)) return false;
            if (seen.Contains(source)) return true;
            path.Add(source);
            var valid = false;
            switch (source.Kind)
            {
                case "Memory":
                    valid = await db.Memories.AsNoTracking().AnyAsync(x => x.Id == source.Id && x.BotId == bot &&
                                                                           (subject == null ||
                                                                            x.SubjectId == subject) &&
                                                                           x.Revision.ToString() == source.Version &&
                                                                           !x.IsOutdated && x.ArchivedAt == null &&
                                                                           x.Status != MemoryStatus.Revoked &&
                                                                           x.Text != "" &&
                                                                           db.LogicalUsers.Any(u =>
                                                                               u.Id == x.SubjectId && u.AllowMemory),
                        ct);
                    break;
                case "Relationship":
                    var relationship = await db.Relationships.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.Id == source.Id &&
                        x.BotId == bot && (subject == null || x.UserId == subject) &&
                        db.LogicalUsers.Any(u => u.Id == x.UserId && u.AllowMemory), ct);
                    valid = relationship is not null &&
                            (await RelationshipClaims.ProjectionAsync(db, relationship, ct)).Source == source;
                    break;
                case "Message":
                    var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.Id &&
                        x.BotId == bot && !x.ContextInvalidated && x.ExpiresAt > instant, ct);
                    valid = message is not null && Message(message) == source &&
                            await MessageValidAsync(db, message, ct, subject, Visit, depth);
                    break;
                case "Summary":
                case "SummaryProjection":
                    var summary = await db.Summaries.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == source.Id && x.BotId == bot, ct);
                    if (summary is null) break;
                    if (source.Kind == "SummaryProjection")
                    {
                        if (Projection(summary) != source || !ProjectionReady(summary, instant)) break;
                        try
                        {
                            var projection = Json.Read<SafeSummaryProjection>(summary.SafeProjectionJson!);
                            valid = projection.Sources is not null && await All(projection.Sources, depth);
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    else if (!summary.ContextInvalidated && Summary(summary) == source)
                    {
                        valid = TryRead(summary.SourcesJson, out var refs)
                            ? await All(refs, depth)
                            : summary.SourcesJson is null && await LegacyValidAsync(db, bot, summary.MemoryIdsJson,
                                summary.MessageIdsJson, ct, subject, Visit, depth);
                        valid = valid &&
                                await NoteSources.ValidAsync(db, NoteDependencyTargetKind.Summary, summary.Id, ct);
                    }

                    break;
                default: return false;
            }

            path.Remove(source);
            if (valid) seen.Add(source);
            return valid;
        }

        async Task<bool> All(IEnumerable<ContentSource> refs, int depth)
        {
            foreach (var source in refs.Distinct())
                if (!await Visit(source, depth + 1))
                    return false;
            return true;
        }

        return await All(sources, 0);
    }

    public static bool ProjectionReady(ConversationSummary row, DateTimeOffset? now = null)
    {
        return row.ProjectionState == "Ready" &&
               row.SafeProjectionJson is not null && row.ProjectionBaseSummaryRevision == row.Revision &&
               row.ProjectionExpiresAt > (now ?? DateTimeOffset.UtcNow);
    }

    public static Task<bool> MessageValidAsync(WonderDbContext db, ChatMessage row, CancellationToken ct,
        Guid? subject = null, DateTimeOffset? now = null)
    {
        return ValidAsync(db, row.BotId, [Message(row)], ct, subject, now);
    }

    private static async Task<bool> MessageValidAsync(WonderDbContext db, ChatMessage row, CancellationToken ct,
        Guid? subject, Func<ContentSource, int, Task<bool>> visit, int depth)
    {
        if (!await NoteSources.ValidAsync(db, NoteDependencyTargetKind.Message, row.Id, ct)) return false;
        if (row.ContextSnapshotJson is null)
            return row.Role == MessageRole.User &&
                   !await db.EvidenceSuppressions.AnyAsync(x => x.BotId == row.BotId && x.MessageId == row.Id, ct);
        try
        {
            var snapshot = Json.Read<ContextSnapshot>(row.ContextSnapshotJson);
            if (snapshot.BotInstanceId != row.BotId) return false;
            if (snapshot.Sources is null)
                return await LegacyValidAsync(db, row.BotId, Json.Write(snapshot.RetrievedMemoryIds),
                    Json.Write(snapshot.SourceMessageIds ?? []), ct, subject, visit, depth);
            foreach (var source in snapshot.Sources)
                if (!await visit(source, depth + 1))
                    return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<bool> LegacyValidAsync(WonderDbContext db, Guid bot, string memories, string messages,
        CancellationToken ct, Guid? subject, Func<ContentSource, int, Task<bool>> visit, int depth)
    {
        if (!ConversationHistory.TryMemoryIds(memories, false, out var memoryIds) ||
            !ConversationHistory.TryMemoryIds(messages, false, out var messageIds)) return false;
        // Empty legacy arrays are missing provenance, not proof that retained model text had no inputs.
        if (memoryIds.Count == 0 && messageIds.Count == 0) return false;
        foreach (var id in memoryIds.Distinct())
        {
            var memory = await db.Memories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.BotId == bot, ct);
            if (memory is null || !await visit(Memory(memory), depth + 1)) return false;
        }

        foreach (var id in messageIds.Distinct())
        {
            var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.BotId == bot, ct);
            if (message is null || !await visit(Message(message), depth + 1)) return false;
        }

        return true;
    }

    public static async Task InvalidateMemoriesAsync(WonderDbContext db, Guid bot, IReadOnlyCollection<Guid> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var messages = await db.Messages.AsNoTracking().Where(x => x.BotId == bot && !x.ContextInvalidated &&
                                                                   x.ContextSnapshotJson != null).ToListAsync(ct);
        var summaries = await db.Summaries.AsNoTracking().Where(x => x.BotId == bot).ToListAsync(ct);
        var affectedMessages = new HashSet<Guid>();
        var affectedSummaries = new HashSet<Guid>();

        bool References(IEnumerable<ContentSource>? refs)
        {
            return refs?.Any(x => x is not null && (
                (x.Kind == "Memory" && ids.Contains(x.Id)) ||
                (x.Kind == "Message" && affectedMessages.Contains(x.Id)) ||
                (x.Kind is "Summary" or "SummaryProjection" && affectedSummaries.Contains(x.Id)))) == true;
        }

        bool IdsIntersect(string? json, IEnumerable<Guid> targets)
        {
            return ConversationHistory.TryMemoryIds(json, false, out var parsed) && parsed.Intersect(targets).Any();
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var row in messages.Where(x => !affectedMessages.Contains(x.Id)))
                try
                {
                    var snapshot = Json.Read<ContextSnapshot>(row.ContextSnapshotJson!);
                    if ((snapshot.Sources is not null && References(snapshot.Sources)) ||
                        snapshot.RetrievedMemoryIds?.Intersect(ids).Any() == true ||
                        snapshot.SourceMessageIds?.Intersect(affectedMessages).Any() == true)
                        changed |= affectedMessages.Add(row.Id);
                }
                catch (JsonException)
                {
                    /* Unknown rows are blocked by the read guard, not a bot-wide mutation. */
                }

            foreach (var row in summaries.Where(x => !affectedSummaries.Contains(x.Id)))
                if (IdsIntersect(row.MemoryIdsJson, ids) || IdsIntersect(row.MessageIdsJson, affectedMessages) ||
                    (TryRead(row.SourcesJson, out var refs) && References(refs)))
                    changed |= affectedSummaries.Add(row.Id);
        } while (changed);

        var messageIds = affectedMessages.ToArray();
        var summaryIds = affectedSummaries.ToArray();
        await db.Messages.Where(x => messageIds.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextInvalidated, true), ct);
        await db.Summaries.Where(x => summaryIds.Contains(x.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ContextInvalidated, true).SetProperty(x => x.SafeProjectionJson, (string?)null)
            .SetProperty(x => x.ProjectionState, "Invalid")
            .SetProperty(x => x.ProjectionRevision, x => x.ProjectionRevision + 1), ct);
        // A rebuilt projection can have different sources from its retained original summary.
        foreach (var row in summaries.Where(x => !affectedSummaries.Contains(x.Id) && x.SafeProjectionJson != null))
        {
            try
            {
                if (!References(Json.Read<SafeSummaryProjection>(row.SafeProjectionJson!).Sources)) continue;
            }
            catch (JsonException)
            {
                continue;
            }

            await db.Summaries.Where(x => x.Id == row.Id && x.ProjectionRevision == row.ProjectionRevision)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SafeProjectionJson, (string?)null)
                    .SetProperty(x => x.ProjectionState, "Invalid")
                    .SetProperty(x => x.ProjectionRevision, x => x.ProjectionRevision + 1), ct);
        }

        var notes = await db.Notes.AsNoTracking().Where(x => x.BotId == bot && !x.PrivacyDeleted).ToListAsync(ct);
        var noteIds = notes.Where(x => IdsIntersect(x.SourceMemoryIdsJson, ids)).Select(x => x.Id).ToArray();
        await db.Notes.Where(x => noteIds.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceInvalidated, true), ct);
        await NoteSources.InvalidateAsync(db, noteIds, false, ct);
        var jobs = messages.Where(x =>
                affectedMessages.Contains(x.Id) &&
                x.ExternalMessageId.StartsWith("generated:", StringComparison.Ordinal))
            .Select(x => Guid.TryParse(x.ExternalMessageId[10..], out var id) ? id : Guid.Empty)
            .Where(x => x != Guid.Empty).ToArray();
        var batches = db.Batches.Where(x => x.BotId == bot && jobs.Contains(x.SourceJobId)).Select(x => x.Id);
        await db.Parts.Where(x => batches.Contains(x.BatchId) && x.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, DeliveryStatus.Failed)
                    .SetProperty(x => x.FailureReason, "MemorySourceInvalid"), ct);
    }
}