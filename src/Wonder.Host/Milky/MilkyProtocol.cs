using System.Globalization;
using SaltyMilky.Net;
using Wonder.Core;

namespace Wonder.Host.Milky;

public static class MilkyProtocol
{
    public static NormalizedEvent? Normalize(long accountId, MilkyEvent envelope)
    {
        if (accountId <= 0 || envelope.SelfId != accountId ||
            envelope.Data is not MilkyMessageReceiveEventData received) return null;
        var message = received.Message;
        var kind = message.MessageScene switch
        {
            "friend" => ConversationKind.Private,
            "group" => ConversationKind.Group,
            _ => (ConversationKind?)null
        };
        if (kind is null) return null;
        var (peer, sender, sequence) = (message.PeerId, message.SenderId, message.MessageSeq);
        if (sender <= 0 || sender == accountId || peer <= 0 || sequence < 0) return null;
        var target = Id(peer);
        var segments = new List<MessageSegment>();
        foreach (var segment in message.Segments)
            switch (segment)
            {
                case MilkyIncomingTextSegment text:
                    segments.Add(new MessageSegment(SegmentKind.Text, text.Text ?? ""));
                    break;
                case MilkyIncomingImageSegment image
                    when !string.IsNullOrWhiteSpace(image.TempUrl):
                    segments.Add(new MessageSegment(SegmentKind.Image, image.TempUrl));
                    break;
                case MilkyIncomingMentionSegment mention:
                    segments.Add(new MessageSegment(SegmentKind.Mention, Id(mention.UserId)));
                    break;
                case MilkyIncomingReplySegment reply:
                    segments.Add(new MessageSegment(SegmentKind.ReplyReference,
                        MessageKey(kind.Value, target, reply.MessageSeq)));
                    break;
            }

        return new NormalizedEvent(Id(accountId), MessageKey(kind.Value, target, sequence), Id(sender), target, kind.Value,
            segments, DateTimeOffset.FromUnixTimeSeconds(message.Time));
    }

    // Milky sequences are scoped to a conversation, including private-message client sequences.
    public static string MessageKey(ConversationKind kind, string target, long sequence)
    {
        return $"{(kind == ConversationKind.Group ? "group" : "private")}:{target}:{Id(sequence)}";
    }

    private static string Id(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
