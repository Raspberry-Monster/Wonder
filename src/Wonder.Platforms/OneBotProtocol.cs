using System.Text.Json;
using Wonder.Core;

namespace Wonder.Platforms;

public static class OneBotProtocol
{
    private static string Value(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var v) ? v.ToString() : "";
    }

    public static NormalizedEvent? Normalize(JsonElement e)
    {
        if (Value(e, "post_type") != "message") return null;
        var kind = Value(e, "message_type") switch
        {
            "private" => ConversationKind.Private, "group" => ConversationKind.Group, _ => (ConversationKind?)null
        };
        if (kind is null || !e.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Array) return null;
        var segments = new List<MessageSegment>();
        foreach (var segment in message.EnumerateArray())
        {
            if (!segment.TryGetProperty("data", out var data)) continue;
            switch (Value(segment, "type"))
            {
                case "text": segments.Add(new MessageSegment(SegmentKind.Text, Value(data, "text"))); break;
                case "image": segments.Add(new MessageSegment(SegmentKind.Image, Value(data, "url"))); break;
                case "at": segments.Add(new MessageSegment(SegmentKind.Mention, Value(data, "qq"))); break;
                case "reply": segments.Add(new MessageSegment(SegmentKind.ReplyReference, Value(data, "id"))); break;
            }
        }

        var sender = Value(e, "user_id");
        var account = Value(e, "self_id");
        var id = Value(e, "message_id");
        if (sender.Length == 0 || account.Length == 0 || id.Length == 0) return null;
        return new NormalizedEvent(account, id, sender, kind == ConversationKind.Group ? Value(e, "group_id") : sender,
            kind.Value, segments,
            e.TryGetProperty("time", out var time) && time.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.UtcNow);
    }

    public static TriggerDecision Decide(NormalizedEvent e, BotInstance bot, GroupPolicy? policy, bool repliesToBot,
        bool consent)
    {
        if (!bot.Enabled || e.SenderId == e.AccountId) return new TriggerDecision(false, false, false);
        var text = string.Concat(e.Segments.Where(x => x.Kind == SegmentKind.Text).Select(x => x.Value)).Trim();
        var respond = e.Kind == ConversationKind.Private || (policy is { } p &&
                                                             ((p.RespondToMention && e.Segments.Any(x =>
                                                                  x.Kind == SegmentKind.Mention &&
                                                                  x.Value == e.AccountId)) ||
                                                              (p.RespondToReply && repliesToBot) ||
                                                              (p.CommandPrefix.Length > 0 &&
                                                               text.StartsWith(p.CommandPrefix,
                                                                   StringComparison.Ordinal))));
        var persist = bot.PersistMessages && (e.Kind == ConversationKind.Private || policy?.PersistMessages == true);
        return new TriggerDecision(persist, respond && persist,
            respond && persist && consent && (e.Kind == ConversationKind.Private || policy?.AllowReflection == true));
    }

    public static IReadOnlyList<string> Split(string text, int maxLength = 1200)
    {
        if (maxLength < 2) throw new ArgumentOutOfRangeException(nameof(maxLength));
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n")
                     .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rest = paragraph;
            while (rest.Length > maxLength)
            {
                var end = rest.LastIndexOfAny(['\n', '。', '！', '？', '.', '!', '?', ' '], maxLength - 1, maxLength);
                if (end < maxLength / 2) end = maxLength;
                else end++;
                if (char.IsHighSurrogate(rest[end - 1])) end--;
                result.Add(rest[..end]);
                rest = rest[end..].TrimStart();
            }

            if (rest.Length > 0) result.Add(rest);
        }

        return result;
    }
}

public interface IPlatformConnection
{
    Task<string> SendAsync(Guid bindingId, ConversationKind kind, string target, string text, CancellationToken ct);
}

public sealed class DeliveryUnknownException(string message, Exception? inner = null) : Exception(message, inner);