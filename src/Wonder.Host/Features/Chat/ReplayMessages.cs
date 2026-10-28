using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Wonder.Core;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using WireMessage = OpenAI.Chat.ChatMessage;

namespace Wonder.Host.Features.Chat;

public sealed record ReplayGroup(long? TurnSequence, Guid[] MessageIds, AiMessage[] Messages, int Weight,
    int HistoryCount = 0);

public sealed record RestoredReplay(ReplayGroup[] Groups, ContentSource[] Sources, Guid[] MemoryIds,
    Guid[] MessageIds, Guid[] UserIds);

public sealed record ReplayMedia(Guid AssetId, string MediaType, string ContentHash);
public sealed record ReplayBlock(string Kind, string? Text = null, string? CallId = null, string? Name = null,
    string? Arguments = null, ReplayMedia? Media = null);
// These are protocol fields, not serialized SDK objects. Preserve raw argument strings and null/content shape.
public sealed record ReplayAssistant(Dictionary<string, JsonElement> Fields);
public sealed record ReplayMessage(string Role, ReplayBlock[] Contents, string? AuthorName = null,
    ReplayAssistant? Assistant = null);
public sealed record ReplayGroupData(int Start, int Count, long? TurnSequence, Guid[] MessageIds, int Weight,
    int HistoryCount);
public sealed record ReplayPayload(int Version, ReplayMessage[] Messages, ReplayGroupData[] Groups,
    ContentSource[] Sources, Guid[] MemoryIds, Guid[] MessageIds, Guid[] UserIds);

public static class ReplayMessages
{
    public const int Version = 1;

    public static ReplayMessage Encode(AiMessage message, Func<DataContent, ReplayMedia?> media)
    {
        if (message.AdditionalProperties is { Count: > 0 } || message.Contents.Any(x =>
                x.AdditionalProperties is { Count: > 0 } || x.Annotations is { Count: > 0 } ||
                x is TextReasoningContent { ProtectedData: not null }))
            throw new NotSupportedException("Unsupported replay extension fields.");
        if (message.Role != ChatRole.System && message.Role != ChatRole.User &&
            message.Role != ChatRole.Assistant && message.Role != ChatRole.Tool)
            throw new NotSupportedException("Unsupported replay role.");
        var blocks = message.Contents.Select<AIContent, ReplayBlock>(content => content switch
        {
            TextContent text => new("text", text.Text),
            TextReasoningContent reasoning => new("reasoning", reasoning.Text),
            FunctionCallContent call when call.Exception is null => new("call", CallId: call.CallId,
                Name: call.Name, Arguments: Json.Write(call.Arguments)),
            FunctionResultContent result when result.Result is string text => new("result", text, result.CallId),
            DataContent data when data.MediaType.StartsWith("image/", StringComparison.Ordinal) =>
                new("image", Media: media(data) ?? throw new NotSupportedException("Untracked replay image.")),
            _ => throw new NotSupportedException("Unsupported replay content.")
        }).ToArray();
        ReplayAssistant? assistant = null;
        if (message.RawRepresentation is { } raw)
        {
            if (message.Role != ChatRole.Assistant || raw is not OpenAI.Chat.AssistantChatMessage wire)
                throw new NotSupportedException("Unsupported replay protocol representation.");
            using var doc = JsonDocument.Parse(ModelReaderWriter.Write(wire, new ModelReaderWriterOptions("W")));
            var fields = new Dictionary<string, JsonElement>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name is not ("role" or "content" or "tool_calls" or "reasoning_content" or "refusal" or "name"))
                    throw new NotSupportedException("Unsupported assistant protocol field.");
                fields.Add(property.Name, property.Value.Clone());
            }
            assistant = new ReplayAssistant(fields);
        }
        return new ReplayMessage(message.Role.Value, blocks, message.AuthorName, assistant);
    }

    public static async Task<AiMessage> DecodeAsync(ReplayMessage dto,
        Func<ReplayMedia, Task<DataContent>> media)
    {
        if (dto.Role is not ("system" or "user" or "assistant" or "tool") || dto.Contents is null)
            throw new NotSupportedException("Unsupported replay message.");
        var message = new AiMessage(new ChatRole(dto.Role), new List<AIContent>()) { AuthorName = dto.AuthorName };
        foreach (var block in dto.Contents)
            message.Contents.Add(block.Kind switch
            {
                "text" => new TextContent(block.Text ?? ""),
                "reasoning" => new TextReasoningContent(block.Text ?? ""),
                "call" when block.CallId is not null && block.Name is not null =>
                    new FunctionCallContent(block.CallId, block.Name,
                        Json.Read<Dictionary<string, object?>>(block.Arguments ?? "{}")),
                "result" when block.CallId is not null => new FunctionResultContent(block.CallId, block.Text ?? ""),
                "image" when block.Media is not null => await media(block.Media),
                _ => throw new NotSupportedException("Unsupported replay block.")
            });
        if (dto.Assistant is { } protocol)
        {
            if (dto.Role != "assistant" || protocol.Fields.Keys.Any(x =>
                    x is not ("role" or "content" or "tool_calls" or "reasoning_content" or "refusal" or "name")) ||
                !protocol.Fields.TryGetValue("role", out var role) || role.GetString() != "assistant")
                throw new NotSupportedException("Unsupported assistant protocol.");
            message.RawRepresentation = ModelReaderWriter.Read<WireMessage>(
                BinaryData.FromString(Json.Write(protocol.Fields)), new ModelReaderWriterOptions("W"));
        }
        return message;
    }

    public static string ImageHash(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes.Span));

    public static void ValidateGroups(ReplayPayload payload)
    {
        if (payload.Version != Version || payload.Messages is not { Length: > 1 } ||
            payload.Messages[0].Role != "system" || payload.Groups is null || payload.Sources is null ||
            payload.MemoryIds is null || payload.MessageIds is null || payload.UserIds is null)
            throw new NotSupportedException("Unsupported replay payload.");
        var next = 1;
        long prior = 0;
        foreach (var group in payload.Groups)
        {
            if (group.Start != next || group.Count <= 0 || group.Count > payload.Messages.Length - next ||
                group.MessageIds is null || group.HistoryCount < 0 || group.HistoryCount > 40 ||
                (group.TurnSequence is { } sequence && sequence <= prior))
                throw new NotSupportedException("Corrupt replay groups.");
            if (group.TurnSequence is { } turn) prior = turn;
            // A complete persisted tool exchange must remain inside a single group.
            var pending = new HashSet<string>();
            foreach (var message in payload.Messages.Skip(next).Take(group.Count))
            {
                if (message.Role == "system") throw new NotSupportedException("Unexpected replay system.");
                foreach (var block in message.Contents)
                {
                    if (block.Kind == "call" && (block.CallId is null || !pending.Add(block.CallId)))
                        throw new NotSupportedException("Invalid replay tool call.");
                    if (block.Kind == "result" && (block.CallId is null || !pending.Remove(block.CallId)))
                        throw new NotSupportedException("Orphan replay tool result.");
                }
            }
            if (pending.Count != 0) throw new NotSupportedException("Incomplete replay tool group.");
            next += group.Count;
        }
        if (next != payload.Messages.Length) throw new NotSupportedException("Uncovered replay messages.");
    }
}
