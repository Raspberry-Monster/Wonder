using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class ContentSourceActors
{
    // Discover locks from concrete consumed resources. Snapshot user hints cannot replace this walk.
    // Callers acquire every returned actor lock in order, then revalidate all original versions.
    public static async Task<Guid[]?> ResolveAsync(WonderDbContext db, Guid bot, IEnumerable<ContentSource> roots,
        CancellationToken ct)
    {
        var actors = new HashSet<Guid>();
        var seen = new HashSet<ContentSource>();
        var path = new HashSet<ContentSource>();

        async Task<bool> Notes(NoteDependencyTargetKind kind, Guid id)
        {
            var owners = await (from d in db.NoteDependencies
                join n in db.Notes on d.NoteId equals n.Id
                where d.TargetKind == kind && d.TargetId == id && n.BotId == bot
                select n.UserId).ToArrayAsync(ct);
            actors.UnionWith(owners);
            return true;
        }

        async Task<bool> Legacy(string memoryJson, string messageJson, int depth)
        {
            if (!ConversationHistory.TryMemoryIds(memoryJson, false, out var memoryIds) ||
                !ConversationHistory.TryMemoryIds(messageJson, false, out var messageIds) ||
                memoryIds.Count + messageIds.Count == 0) return false;
            var memories = await db.Memories.AsNoTracking().Where(x => memoryIds.Contains(x.Id) && x.BotId == bot)
                .ToListAsync(ct);
            var messages = await db.Messages.AsNoTracking().Where(x => messageIds.Contains(x.Id) && x.BotId == bot)
                .ToListAsync(ct);
            return memories.Count == memoryIds.Distinct().Count() && messages.Count == messageIds.Distinct().Count() &&
                   await All(memories.Select(ContentSources.Memory).Concat(messages.Select(ContentSources.Message)),
                       depth);
        }

        async Task<bool> Visit(ContentSource source, int depth)
        {
            if (source is null || depth > 16 || seen.Count > 512 || path.Contains(source)) return false;
            if (seen.Contains(source)) return true;
            path.Add(source);
            var valid = false;
            switch (source.Kind)
            {
                case "Memory":
                    var memory = await db.Memories.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == source.Id && x.BotId == bot, ct);
                    if (memory is not null && ContentSources.Memory(memory) == source)
                    {
                        actors.Add(memory.SubjectId);
                        valid = true;
                    }

                    break;
                case "Relationship":
                    var relationship = await db.Relationships.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == source.Id && x.BotId == bot, ct);
                    if (relationship is not null)
                    {
                        actors.Add(relationship.UserId);
                        valid = true;
                    }

                    break;
                case "Message":
                    var message = await db.Messages.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == source.Id && x.BotId == bot, ct);
                    if (message is null || ContentSources.Message(message) != source) break;
                    if (message.SenderId is { } sender) actors.Add(sender);
                    if (message.ContextSnapshotJson is null)
                    {
                        valid = message.Role == MessageRole.User;
                    }
                    else
                    {
                        var snapshot = Json.Read<ContextSnapshot>(message.ContextSnapshotJson);
                        valid = snapshot.BotInstanceId == bot && (snapshot.Sources is { } sources
                            ? await All(sources, depth)
                            : await Legacy(Json.Write(snapshot.RetrievedMemoryIds),
                                Json.Write(snapshot.SourceMessageIds ?? []), depth));
                    }

                    valid = valid && await Notes(NoteDependencyTargetKind.Message, message.Id);
                    break;
                case "Summary":
                case "SummaryProjection":
                    var summary = await db.Summaries.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == source.Id && x.BotId == bot, ct);
                    if (summary is null) break;
                    if (source.Kind == "SummaryProjection")
                        valid = ContentSources.Projection(summary) == source &&
                                summary.SafeProjectionJson is not null &&
                                await All(Json.Read<SafeSummaryProjection>(summary.SafeProjectionJson).Sources, depth);
                    else
                        valid = ContentSources.Summary(summary) == source &&
                                (ContentSources.TryRead(summary.SourcesJson, out var sources)
                                    ? await All(sources, depth)
                                    : summary.SourcesJson is null && await Legacy(summary.MemoryIdsJson,
                                        summary.MessageIdsJson, depth)) &&
                                await Notes(NoteDependencyTargetKind.Summary, summary.Id);
                    break;
            }

            path.Remove(source);
            if (valid) seen.Add(source);
            return valid;
        }

        async Task<bool> All(IEnumerable<ContentSource> sources, int depth)
        {
            foreach (var source in sources.Distinct())
                if (!await Visit(source, depth + 1))
                    return false;
            return true;
        }

        try
        {
            return await All(roots, 0) ? actors.Order().ToArray() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}