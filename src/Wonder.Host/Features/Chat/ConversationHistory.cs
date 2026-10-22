using Wonder.Infrastructure;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using ChatMessage = Wonder.Core.ChatMessage;

namespace Wonder.Host;

public sealed class ConversationHistory(IDbContextFactory<WonderDbContext> factory, ConversationClock clock)
{
    public static IQueryable<ChatMessage> Visible(WonderDbContext db, Guid botId, Guid conversationId, long beforeSequence, DateTimeOffset now) =>
        db.Messages.AsNoTracking().Where(x => x.BotId == botId && x.ConversationId == conversationId &&
            !x.ContextInvalidated && x.ExpiresAt > now && (x.InReplyToSequence ?? x.Sequence) < beforeSequence);

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
        catch (JsonException) { return false; }
    }

    public static IReadOnlyList<Guid> MessageIds(ChatMessage message)
    {
        var inherited = message.ContextSnapshotJson is { Length: > 0 } json
            ? Json.Read<ContextSnapshot>(json).SourceMessageIds ?? [] : [];
        return inherited.Append(message.Id).Distinct().ToArray();
    }

    public static IReadOnlyList<Guid> SubjectIds(ChatMessage message)
    {
        var inherited = message.ContextSnapshotJson is { Length: > 0 } json
            ? Json.Read<ContextSnapshot>(json).SourceUserIds ?? [] : [];
        return (message.SenderId is { } sender ? inherited.Append(sender) : inherited).Distinct().ToArray();
    }

    public static async Task<ConversationSummary?> LatestSummaryAsync(WonderDbContext db, Guid botId,
        Guid conversationId, long beforeSequence, CancellationToken ct)
    {
        var candidates = await (from summary in db.Summaries.AsNoTracking()
            join session in db.Sessions on summary.SessionId equals session.Id
            where summary.BotId == botId && session.BotId == botId && session.ConversationId == conversationId &&
                !summary.ContextInvalidated && summary.ThroughSequence < beforeSequence
            orderby summary.ThroughSequence descending, summary.Id
            select summary).Take(20).ToListAsync(ct);
        return candidates.FirstOrDefault(x => TryMemoryIds(x.MemoryIdsJson, false, out _) &&
            TryMemoryIds(x.SubjectIdsJson, false, out _) && TryMemoryIds(x.MessageIdsJson, false, out _));
    }

    public async Task<(ConversationSummary? Summary, IReadOnlyList<ChatMessage> Messages)> LoadAsync(
        Guid botId, Guid conversationId, long beforeSequence, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var summary = await LatestSummaryAsync(db, botId, conversationId, beforeSequence, ct);
        var through = summary?.ThroughSequence ?? 0;
        var rows = await Visible(db, botId, conversationId, beforeSequence, clock.UtcNow)
            .Where(x => (x.InReplyToSequence ?? x.Sequence) > through)
            .OrderByDescending(x => x.InReplyToSequence ?? x.Sequence).ThenByDescending(x => x.Role == "assistant")
            .ThenByDescending(x => x.Sequence).ThenBy(x => x.Id).Take(40).ToListAsync(ct);
        rows.Reverse();
        return (summary, rows.Where(x => TryMemoryIds(x.ContextSnapshotJson, true, out _)).ToArray());
    }
}
