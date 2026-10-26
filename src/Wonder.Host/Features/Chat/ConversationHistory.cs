using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;
using ChatMessage = Wonder.Core.ChatMessage;

namespace Wonder.Host;

public sealed class ConversationHistory(IDbContextFactory<WonderDbContext> factory, ConversationClock clock)
{
    public static IQueryable<ChatMessage> Visible(WonderDbContext db, Guid botId, Guid conversationId,
        long beforeSequence, DateTimeOffset now)
    {
        return db.Messages.AsNoTracking().Where(x => x.BotId == botId && x.ConversationId == conversationId &&
                                                     db.Conversations.Any(c =>
                                                         c.Id == conversationId &&
                                                         c.Kind == ConversationKind.Private) &&
                                                     !x.ContextInvalidated && x.ExpiresAt > now &&
                                                     (x.InReplyToSequence ?? x.Sequence) < beforeSequence);
    }

    public static bool TryMemoryIds(string? json, bool snapshot, out IReadOnlyList<Guid> ids)
    {
        ids = [];
        if (json is null && snapshot) return true;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var parsed = snapshot ? Json.Read<ContextSnapshot>(json).RetrievedMemoryIds : Json.Read<Guid[]>(json);
            if (parsed is null) return false;
            ids = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<Guid> MessageIds(ChatMessage message)
    {
        var inherited = message.ContextSnapshotJson is { Length: > 0 } json
            ? Json.Read<ContextSnapshot>(json).SourceMessageIds ?? []
            : [];
        return inherited.Append(message.Id).Distinct().ToArray();
    }

    public static IReadOnlyList<Guid> SubjectIds(ChatMessage message)
    {
        var inherited = message.ContextSnapshotJson is { Length: > 0 } json
            ? Json.Read<ContextSnapshot>(json).SourceUserIds ?? []
            : [];
        return (message.SenderId is { } sender ? inherited.Append(sender) : inherited).Distinct().ToArray();
    }

    public static async Task<ConversationSummary?> LatestSummaryAsync(WonderDbContext db, Guid botId,
        Guid conversationId, long beforeSequence, CancellationToken ct, DateTimeOffset? now = null)
    {
        var candidates = await (from summary in db.Summaries.AsNoTracking()
            join session in db.Sessions on summary.SessionId equals session.Id
            where summary.BotId == botId && session.BotId == botId && session.ConversationId == conversationId &&
                  summary.ThroughSequence < beforeSequence
                  && session.ConversationId != Guid.Empty && db.Conversations.Any(c =>
                      c.Id == session.ConversationId && c.Kind == ConversationKind.Private)
            orderby summary.ThroughSequence descending, summary.Id
            select summary).Take(20).ToListAsync(ct);
        foreach (var row in candidates)
        {
            var source = ContentSources.ProjectionReady(row, now)
                ? ContentSources.Projection(row)
                : ContentSources.Summary(row);
            if (TryMemoryIds(row.SubjectIdsJson, false, out _) &&
                await ContentSources.ValidAsync(db, botId, [source], ct, now: now)) return row;
        }

        return null;
    }

    public static string TextForModel(ConversationSummary summary)
    {
        return ContentSources.ProjectionReady(summary)
            ? Json.Read<SafeSummaryProjection>(summary.SafeProjectionJson!).Text
            : summary.Text;
    }

    public static IReadOnlyList<Guid> MemoryIdsForModel(ConversationSummary summary)
    {
        return ContentSources.ProjectionReady(summary)
            ? Json.Read<SafeSummaryProjection>(summary.SafeProjectionJson!).Sources.Where(x => x.Kind == "Memory")
                .Select(x => x.Id).Distinct().ToArray()
            : Json.Read<Guid[]>(summary.MemoryIdsJson);
    }

    public static IReadOnlyList<Guid> MessageIdsForModel(ConversationSummary summary)
    {
        return ContentSources.ProjectionReady(summary)
            ? Json.Read<SafeSummaryProjection>(summary.SafeProjectionJson!).Sources.Where(x => x.Kind == "Message")
                .Select(x => x.Id).Distinct().ToArray()
            : Json.Read<Guid[]>(summary.MessageIdsJson);
    }

    public async Task<(ConversationSummary? Summary, IReadOnlyList<ChatMessage> Messages)> LoadAsync(
        Guid botId, Guid conversationId, long beforeSequence, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var summary = await LatestSummaryAsync(db, botId, conversationId, beforeSequence, ct, clock.UtcNow);
        var through = summary?.ThroughSequence ?? 0;
        var rows = await Visible(db, botId, conversationId, beforeSequence, clock.UtcNow)
            .Where(x => (x.InReplyToSequence ?? x.Sequence) > through)
            .OrderByDescending(x => x.InReplyToSequence ?? x.Sequence)
            .ThenByDescending(x => x.Role == MessageRole.Assistant)
            .ThenByDescending(x => x.Sequence).ThenBy(x => x.Id).Take(40).ToListAsync(ct);
        rows.Reverse();
        var safe = new List<ChatMessage>();
        foreach (var row in rows)
            if (await ContentSources.MessageValidAsync(db, row, ct, now: clock.UtcNow))
                safe.Add(row);
        return (summary, safe);
    }
}