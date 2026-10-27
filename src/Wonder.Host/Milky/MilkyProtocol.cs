using System.Globalization;
using Milky.Net.Model;
using Wonder.Core;

namespace Wonder.Host.Milky;

public static class MilkyProtocol
{
    public static NormalizedEvent? Normalize(long accountId, Event envelope)
    {
        if (accountId <= 0 || envelope.SelfId != accountId || envelope is not Event<IncomingMessage> received) return null;
        var (kind, peer, sender, sequence, time, incoming) = received.Data switch
        {
            FriendIncomingMessage m => (ConversationKind.Private, m.PeerId, m.SenderId, m.MessageSeq, m.Time, m.Segments),
            GroupIncomingMessage m => (ConversationKind.Group, m.PeerId, m.SenderId, m.MessageSeq, m.Time, m.Segments),
            _ => default
        };
        if (incoming is null) return null;
        if (sender <= 0 || sender == accountId || peer <= 0 || sequence < 0) return null;
        var target = Id(peer);
        var segments = new List<MessageSegment>();
        foreach (var segment in incoming)
        {
            switch (segment)
            {
                case IncomingSegment<TextIncomingSegmentData> text:
                    segments.Add(new MessageSegment(SegmentKind.Text, text.Data.Text ?? ""));
                    break;
                case IncomingSegment<ImageIncomingSegmentData> image when !string.IsNullOrWhiteSpace(image.Data.TempUrl):
                    segments.Add(new MessageSegment(SegmentKind.Image, image.Data.TempUrl));
                    break;
                case IncomingSegment<MentionIncomingSegmentData> mention:
                    segments.Add(new MessageSegment(SegmentKind.Mention, Id(mention.Data.UserId)));
                    break;
                case IncomingSegment<ReplyIncomingSegmentData> reply:
                    segments.Add(new MessageSegment(SegmentKind.ReplyReference,
                        MessageKey(kind, target, reply.Data.MessageSeq)));
                    break;
            }
        }

        return new NormalizedEvent(Id(accountId), MessageKey(kind, target, sequence), Id(sender), target, kind,
            segments, time);
    }

    // Milky sequences are scoped to a conversation, including private-message client sequences.
    public static string MessageKey(ConversationKind kind, string target, long sequence) =>
        $"{(kind == ConversationKind.Group ? "group" : "private")}:{target}:{Id(sequence)}";

    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
}
