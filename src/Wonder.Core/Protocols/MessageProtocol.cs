namespace Wonder.Core.Protocols;

public static class MessageProtocol
{
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
            respond && persist && consent && e.Kind == ConversationKind.Private);
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