using System.Text.Json;
using System.Text.Json.Nodes;
using SaltyMilky.Net;
using Wonder.Core;
using Wonder.Host.Milky;

namespace Wonder.ContractTests;

public sealed class MilkyProtocolTests
{
    [Test]
    public async Task MilkyNormalizesMixedSegmentsAndUsesMessageTime()
    {
        using var document = JsonDocument.Parse("""
                                                {"self_id":123,"time":1792454500,"event_type":"message_receive","data":{
                                                  "message_scene":"group","peer_id":789,"sender_id":456,"message_seq":100,"time":1792454400,
                                                  "segments":[{"type":"text","data":{"text":"你好"}},
                                                    {"type":"mention","data":{"user_id":123}},
                                                    {"type":"image","data":{"temp_url":"https://example.com/a.png","resource_id":"image"}},
                                                    {"type":"reply","data":{"message_seq":99,"sender_id":123}},
                                                    {"type":"face","data":{"face_id":"1"}}]}}
                                                """);
        var e = MilkyProtocol.Normalize(123,
            document.RootElement.Deserialize(MilkyJson.EventTypeInfo)!)!;
        await Assert.That(e.AccountId).IsEqualTo("123");
        await Assert.That(e.ConversationId).IsEqualTo("789");
        await Assert.That(e.SenderId).IsEqualTo("456");
        await Assert.That(e.Kind).IsEqualTo(ConversationKind.Group);
        await Assert.That(e.MessageId).IsEqualTo("group:789:100");
        await Assert.That(e.OccurredAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1792454400));
        await Assert.That(e.Segments.Select(x => x.Kind).SequenceEqual(new[]
            { SegmentKind.Text, SegmentKind.Mention, SegmentKind.Image, SegmentKind.ReplyReference })).IsTrue();
        await Assert.That(e.Segments.Last().Value).IsEqualTo("group:789:99");
    }

    [Test]
    public async Task MilkyScopesSequencesAndRejectsForeignSelfAndTemporaryMessages()
    {
        var message = JsonNode.Parse("""
                                     {"self_id":123,"event_type":"message_receive","data":{"message_scene":"group",
                                     "peer_id":789,"sender_id":456,"message_seq":100,"time":1792454400,"segments":[]}}
                                     """)!;

        NormalizedEvent? Normalize()
        {
            return MilkyProtocol.Normalize(123, message.Deserialize(MilkyJson.EventTypeInfo)!);
        }

        var groupA = Normalize()!;
        message["data"]!["peer_id"] = 987;
        var groupB = Normalize()!;
        message["data"]!["message_scene"] = "friend";
        message["data"]!["peer_id"] = 456;
        var friend = Normalize()!;
        await Assert.That(new[] { groupA.MessageId, groupB.MessageId, friend.MessageId }.Distinct().Count())
            .IsEqualTo(3);
        await Assert.That(friend.Kind).IsEqualTo(ConversationKind.Private);
        message["self_id"] = 999;
        await Assert.That(Normalize()).IsNull();
        message["self_id"] = 123;
        message["data"]!["sender_id"] = 123;
        await Assert.That(Normalize()).IsNull();
        message["data"]!["sender_id"] = 456;
        message["data"]!["message_scene"] = "temp";
        await Assert.That(Normalize()).IsNull();
        message["event_type"] = "message_recall";
        await Assert.That(Normalize()).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MilkyAcceptsFlatSegmentsAndWrappedMessagePayloads(bool wrapped)
    {
        var message = JsonNode.Parse("""
                                     {"message_scene":"friend","peer_id":456,"sender_id":456,
                                      "message_seq":4294967296,"time":1792454400,
                                      "message_style":{"bubble_id":123},
                                      "segments":[{"type":"text","text":"兼容消息"},
                                        {"type":"mention","user_id":123},
                                        {"type":"reply","message_seq":4294967295,"sender_id":123}]}
                                     """)!;
        var envelope = new JsonObject
        {
            ["self_id"] = 123,
            ["event_type"] = "message_receive",
            ["data"] = wrapped ? new JsonObject { ["message"] = message } : message
        };
        var result = MilkyProtocol.Normalize(123, envelope.Deserialize(MilkyJson.EventTypeInfo)!)!;
        await Assert.That(result.MessageId).IsEqualTo("private:456:4294967296");
        await Assert.That(result.OccurredAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1792454400));
        await Assert.That(result.Segments.Select(x => x.Value).SequenceEqual(new[]
            { "兼容消息", "123", "private:456:4294967295" })).IsTrue();
    }

    [Test]
    [Arguments("relative/path")]
    [Arguments("ws://milky:3000")]
    [Arguments("file:///tmp/milky")]
    [Arguments("http://user:password@milky:3000")]
    [Arguments("http://milky:3000?access_token=secret")]
    [Arguments("http://milky:3000/api")]
    [Arguments("http://milky:3000/event/")]
    public async Task MilkyRejectsInvalidBaseUrls(string url)
    {
        await Assert.That(MilkyConnection.IsValidBaseUrl(url)).IsFalse();
    }
}
