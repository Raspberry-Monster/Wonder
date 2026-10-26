using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed record PrivacyPlan(
    Guid BotId,
    PrivacyTarget[] Targets,
    SuppressedEvidence[] Suppressions,
    EraseMessageView[] SourceMessages,
    string ScopeHash);

public static class PrivacyPlans
{
    public static async Task<PrivacyPlan> BuildAsync(WonderDbContext db, Guid actor, Guid memoryId,
        long expectedRevision,
        bool erase, IReadOnlyCollection<Guid> selectedMessages, CancellationToken ct, string? mediaRoot = null)
    {
        var selected = await db.Memories.AsNoTracking()
                           .SingleOrDefaultAsync(x => x.Id == memoryId && x.SubjectId == actor, ct)
                       ?? throw new KeyNotFoundException();
        if (selected.Status == MemoryStatus.Revoked && !erase &&
            !await db.EvidenceSuppressions.AnyAsync(x => x.MemoryId == memoryId && x.SubjectId == actor, ct))
            throw new KeyNotFoundException();
        if (selected.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("Memory changed; preview again.");
        var scope = await db.Memories.AsNoTracking().Where(x => x.BotId == selected.BotId && x.SubjectId == actor)
            .ToListAsync(ct);
        var family = new HashSet<Guid> { selected.Id };
        bool changed;
        do
        {
            changed = false;
            foreach (var row in scope.Where(x => x.SupersedesMemoryId is not null))
                if (family.Contains(row.Id) || family.Contains(row.SupersedesMemoryId!.Value))
                {
                    changed |= family.Add(row.Id);
                    changed |= family.Add(row.SupersedesMemoryId!.Value);
                }
        } while (changed);

        var members = scope.Where(x => family.Contains(x.Id)).OrderBy(x => x.Id).ToArray();
        if (members.Length != family.Count ||
            members.GroupBy(x => x.SupersedesMemoryId).Any(g => g.Key != null && g.Count() > 1))
            throw new DbUpdateConcurrencyException("Broken or branched family; review required.");
        foreach (var row in members)
        {
            var seen = new HashSet<Guid>();
            var cursor = row;
            while (true)
            {
                if (!seen.Add(cursor.Id)) throw new DbUpdateConcurrencyException("Cyclic memory lineage.");
                if (cursor.SupersedesMemoryId is not { } parent) break;
                cursor = members.Single(x => x.Id == parent);
            }
        }

        var targets = members.Select(x => new PrivacyTarget("Memory", x.Id,
            x.Revision.ToString(CultureInfo.InvariantCulture))).ToList();
        var evidence = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var row in members)
        {
            if (!ConversationHistory.TryMemoryIds(row.EvidenceJson, false, out var ids))
                throw new DbUpdateConcurrencyException("Memory evidence is corrupt; exact scope is unknown.");
            evidence[row.Id] = row.Status == MemoryStatus.Revoked
                ? await db.EvidenceSuppressions.Where(x => x.MemoryId == row.Id &&
                                                           x.BotId == selected.BotId && x.SubjectId == actor)
                    .Select(x => x.MessageId).Distinct().ToArrayAsync(ct)
                : ids.Distinct().ToArray();
        }

        var sourceIds = evidence.Values.SelectMany(x => x).Distinct().ToArray();
        var messages = await db.Messages.AsNoTracking().Where(x =>
            sourceIds.Contains(x.Id) && x.BotId == selected.BotId &&
            (x.Text != "" || x.SegmentsJson != "[]") && x.SenderId == actor && x.Role == MessageRole.User &&
            db.Conversations.Any(c => c.Id == x.ConversationId &&
                                      c.BotId == selected.BotId && c.Kind == ConversationKind.Private)).ToListAsync(ct);
        if (erase && (selectedMessages.Count == 0 || selectedMessages.Except(messages.Select(x => x.Id)).Any()))
            throw new ArgumentException("Select retained original private messages from this authorized preview.");
        var retainedRows = await db.EvidenceSuppressions.AsNoTracking()
            .Where(x => family.Contains(x.MemoryId) && x.SubjectId == actor && x.BotId == selected.BotId)
            .ToListAsync(ct);
        var retainedSuppressions = retainedRows.Select(x => new SuppressedEvidence(x.MemoryId, x.MessageId,
            x.MessageVersion, x.FactKeyHash, Json.Read<EvidenceSpan[]>(x.SpansJson))).ToArray();
        var suppressions = members.Where(m => m.Status != MemoryStatus.Revoked).SelectMany(m => evidence[m.Id]
                .Select(id => new SuppressedEvidence(m.Id, id,
                    messages.FirstOrDefault(x => x.Id == id) is { } message
                        ? ContentSources.Message(message).Version
                        : "Unknown",
                    m.FactKeyHash.Length == 64 ? m.FactKeyHash : Json.Hash(m.FactKey),
                    Spans(messages.FirstOrDefault(x => x.Id == id)?.Text, m.Text)))).Concat(retainedSuppressions)
            .DistinctBy(x => (x.MemoryId, x.MessageId)).OrderBy(x => x.MemoryId).ThenBy(x => x.MessageId).ToArray();
        var candidates = await db.MemoryCandidates.AsNoTracking().Where(x =>
            x.BotId == selected.BotId && x.SubjectId == actor &&
            ((x.ResultMemoryId != null && family.Contains(x.ResultMemoryId.Value)) ||
             (x.TargetMemoryId != null && family.Contains(x.TargetMemoryId.Value)))).ToListAsync(ct);
        targets.AddRange(candidates.Select(x => new PrivacyTarget("Candidate", x.Id, Json.Hash(Json.Write(x)))));
        var reflections = members.Select(x => x.SourceReflectionId).Concat(candidates.Select(x => x.SourceReflectionId))
            .Where(x => x != Guid.Empty).Distinct().ToArray();
        var producers = await db.Reflections.AsNoTracking()
            .Where(x => reflections.Contains(x.Id) && x.BotId == selected.BotId && x.SubjectId == actor)
            .ToListAsync(ct);
        targets.AddRange(producers.Select(x => new PrivacyTarget("ReflectionMemory", x.Id, Json.Hash(x.Payload))));
        var summaries = await db.Summaries.AsNoTracking()
            .Where(x => x.BotId == selected.BotId && x.SafeProjectionJson != null).ToListAsync(ct);
        foreach (var summary in summaries)
            try
            {
                if (Json.Read<SafeSummaryProjection>(summary.SafeProjectionJson!).Sources
                    .Any(x => x.Kind == "Memory" && family.Contains(x.Id)))
                    targets.Add(new PrivacyTarget("SummaryProjection", summary.Id,
                        summary.ProjectionRevision.ToString()));
            }
            catch (JsonException)
            {
            }

        if (erase)
        {
            var erasedMessages = selectedMessages.ToHashSet();
            var erasedSummaries = new HashSet<Guid>();
            var allMessages = await db.Messages.AsNoTracking().Where(x =>
                x.BotId == selected.BotId && x.Role == MessageRole.Assistant &&
                x.ContextSnapshotJson != null).ToListAsync(ct);
            var allSummaries = await db.Summaries.AsNoTracking().Where(x => x.BotId == selected.BotId).ToListAsync(ct);
            do
            {
                changed = false;
                foreach (var row in allMessages.Where(x => !erasedMessages.Contains(x.Id)))
                    try
                    {
                        var snapshot = Json.Read<ContextSnapshot>(row.ContextSnapshotJson!);
                        if (snapshot.SourceMessageIds?.Any(erasedMessages.Contains) == true ||
                            snapshot.Sources?.Any(x => (x.Kind == "Message" && erasedMessages.Contains(x.Id)) ||
                                                       (x.Kind is "Summary" or "SummaryProjection" &&
                                                        erasedSummaries.Contains(x.Id))) == true)
                            changed |= erasedMessages.Add(row.Id);
                    }
                    catch (JsonException)
                    {
                    }

                foreach (var row in allSummaries.Where(x => !erasedSummaries.Contains(x.Id)))
                    if ((ConversationHistory.TryMemoryIds(row.MessageIdsJson, false, out var ids) &&
                         ids.Any(erasedMessages.Contains)) ||
                        (ContentSources.TryRead(row.SourcesJson, out var refs) && refs.Any(x =>
                            (x.Kind == "Message" && erasedMessages.Contains(x.Id)) ||
                            (x.Kind == "Summary" && erasedSummaries.Contains(x.Id)))))
                        changed |= erasedSummaries.Add(row.Id);
            } while (changed);

            var originals = messages.Where(x => selectedMessages.Contains(x.Id));
            targets.AddRange(originals.Concat(allMessages.Where(x => erasedMessages.Contains(x.Id))).Select(x =>
                new PrivacyTarget("Message", x.Id, ContentSources.Message(x).Version)));
            targets.AddRange(allSummaries.Where(x => erasedSummaries.Contains(x.Id))
                .Select(x => new PrivacyTarget("Summary", x.Id, x.Revision.ToString())));
            var media = await db.Media.AsNoTracking()
                .Where(x => x.BotId == selected.BotId && erasedMessages.Contains(x.MessageId)).ToListAsync(ct);
            targets.AddRange(media.Select(x => new PrivacyTarget("Media", x.Id,
                Json.Hash(Json.Write(new { x.MessageId, x.Path, x.Hash })),
                mediaRoot is null ? null : Path.GetRelativePath(mediaRoot, x.Path), x.ContentHash)));
            var generatedJobs = allMessages.Where(x =>
                    erasedMessages.Contains(x.Id) &&
                    x.ExternalMessageId.StartsWith("generated:", StringComparison.Ordinal))
                .Select(x => Guid.TryParse(x.ExternalMessageId[10..], out var id) ? id : Guid.Empty)
                .Where(x => x != Guid.Empty).ToArray();
            var batchIds = db.Batches.Where(x => x.BotId == selected.BotId && generatedJobs.Contains(x.SourceJobId))
                .Select(x => x.Id);
            var parts = await db.Parts.AsNoTracking().Where(x => batchIds.Contains(x.BatchId)).ToListAsync(ct);
            targets.AddRange(parts.Select(x => new PrivacyTarget("OutgoingPart", x.Id, Json.Hash(x.Text))));
        }

        // Only drafts that explicitly recorded a consumed root are relevant; opaque drafts are not guessed from user/session.
        var drafts = await db.Outbox.AsNoTracking().Where(x => x.BotId == selected.BotId && x.ProgressJson != null)
            .ToListAsync(ct);
        foreach (var draft in drafts)
        {
            if (draft.Type == JobKind.Summary && TrySummaryDraft(draft.ProgressJson!, family, selectedMessages))
                targets.Add(new PrivacyTarget("SummaryDraft", draft.Id, Json.Hash(draft.ProgressJson!)));
            if (draft.Type == JobKind.Reflection && TryReflectionDraft(draft.ProgressJson!,
                    candidates.Select(x => x.CandidateHash).ToHashSet()))
                targets.Add(new PrivacyTarget("ReflectionDraft", draft.Id, Json.Hash(draft.ProgressJson!)));
        }

        var ordered = targets.DistinctBy(x => (x.Kind, x.Id)).OrderBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Id).ToArray();
        return new PrivacyPlan(selected.BotId, ordered, suppressions,
            messages.Select(x =>
                    new EraseMessageView(x.Id, x.BotId, actor, x.CreatedAt, x.Text, ContentSources.Message(x).Version))
                .ToArray(),
            Json.Hash(Json.Write(new { actor, memoryId, expectedRevision, erase, targets = ordered, suppressions })));
    }

    private static EvidenceSpan[] Spans(string? source, string assertion)
    {
        if (source is null || string.IsNullOrWhiteSpace(assertion)) return [];
        var spans = new List<EvidenceSpan>();
        for (var offset = 0; offset <= source.Length - assertion.Length;)
        {
            var index = source.IndexOf(assertion, offset, StringComparison.Ordinal);
            if (index < 0) break;
            spans.Add(new EvidenceSpan(index, assertion.Length));
            offset = index + assertion.Length;
        }

        return spans.ToArray();
    }

    private static bool TryReflectionDraft(string json, IReadOnlySet<string> hashes)
    {
        try
        {
            return Json.Read<ReflectionProgress>(json).Proposal.Memories
                .Any(x => MemoryAssertionPayload.Matches(x, hashes));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TrySummaryDraft(string json, IReadOnlySet<Guid> memories, IReadOnlyCollection<Guid> messages)
    {
        try
        {
            var draft = Json.Read<SummaryProgress>(json);
            return draft.MemoryIds.Any(memories.Contains) || draft.MessageIds.Any(messages.Contains) ||
                   draft.Sources.Any(x =>
                       (x.Kind == "Memory" && memories.Contains(x.Id)) ||
                       (x.Kind == "Message" && messages.Contains(x.Id)));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}