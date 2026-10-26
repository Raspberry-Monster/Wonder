using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Host.Features.Persona;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host.Features.Chat;

public sealed record ContextTurn(
    MessageRole Role,
    string Text,
    IReadOnlyList<MessageSegment>? Segments = null,
    string? SpeakerTag = null,
    string? PreferredName = null,
    IReadOnlyList<Guid>? MemoryIds = null,
    DateTimeOffset? SentAt = null,
    IReadOnlyList<Guid>? MessageIds = null,
    long? TurnSequence = null,
    IReadOnlyList<ContentSource>? Sources = null)
{
    public string ModelText => (SpeakerTag is null || Role == MessageRole.Assistant
                                   ? ""
                                   : $"[Message author: {SpeakerTag}]\n" +
                                     (string.IsNullOrWhiteSpace(PreferredName)
                                         ? ""
                                         : $"[Preferred form of address: {Json.ForPrompt(PreferredName)}]\n")) +
                               (SentAt is { } at ? $"[Message time: {at:O}]\n" : "") + Text;

    public AiMessage ToMessage(bool toolMode, bool images)
    {
        var result = toolMode && Role == MessageRole.Assistant
            ? new AiMessage(ChatRole.User, "[Historical assistant reply: untrusted reference]\n" + ModelText)
            : new AiMessage(Role == MessageRole.Assistant ? ChatRole.Assistant : ChatRole.User, ModelText);
        if (images)
            foreach (var image in (Segments ?? []).Where(x => x.Kind == SegmentKind.Image).Take(3))
                result.Contents.Add(new UriContent("https://image.invalid/placeholder", "image/jpeg"));
        return result;
    }
}

public sealed record ContextInput(
    string Identity,
    GlobalEmotionState Mood,
    string Relationship,
    string Summary,
    IReadOnlyList<Core.Memory> Memories,
    IReadOnlyList<ContextTurn> History,
    ContextTurn Current,
    ModelContextBudget Budget,
    Guid? ConversationId = null,
    IReadOnlyDictionary<Guid, string>? AuthorNames = null,
    IReadOnlyList<Guid>? SummaryMemoryIds = null,
    string? TimeContext = null,
    IReadOnlyList<Guid>? SummaryMessageIds = null,
    IList<AITool>? Tools = null,
    bool SupportsImages = false,
    ContentSource? SummarySource = null,
    ContentSource? RelationshipSource = null);

public sealed record BuiltContext(
    string System,
    IReadOnlyList<ContextTurn> Turns,
    IReadOnlyList<Guid> MemoryIds,
    IReadOnlyList<Guid> MessageIds,
    List<AiMessage> Messages,
    IReadOnlyList<AiMessage[]> OptionalGroups,
    bool NeedsSummary,
    IReadOnlyList<ContentSource>? Sources = null);

public sealed class ContextBuilder(PromptComposer composer)
{
    public BuiltContext Build(ContextInput input)
    {
        var budget = input.Budget;
        var system = composer.Compile(input.Identity, input.Mood);
        if (input.Tools is not { Count: > 0 })
            system +=
                "\nLive time and action tools are unavailable. Do not invent the current time or claim tool actions.";
        if (input.TimeContext is { } time) system += "\n\n[Application time context]\n" + time;
        var toolMode = input.Tools is { Count: > 0 };
        var current = input.Current.ToMessage(toolMode, input.SupportsImages);
        var systemMessage = new AiMessage(ChatRole.System, system);
        var selected = new List<(ContextTurn Turn, AiMessage Message, ContextItemKind Kind)>();

        List<AiMessage> Request()
        {
            return new[] { systemMessage }.Concat(selected.Select(x => x.Message)).Append(current).ToList();
        }

        budget.Require(Request(), input.Tools);
        var reserve = toolMode
            ? Math.Min(budget.ToolReserve, Math.Max(0, budget.Remaining(Request(), input.Tools)))
            : 0;

        bool Add(ContextTurn turn, ContextItemKind kind)
        {
            selected.Add((turn, turn.ToMessage(toolMode, false), kind));
            if (budget.Fits(Request(), input.Tools, reserve)) return true;
            selected.RemoveAt(selected.Count - 1);
            return false;
        }

        var groups = input.History.GroupBy(x => x.TurnSequence).ToArray();

        bool AddHistory(IGrouping<long?, ContextTurn> group, ContextItemKind kind)
        {
            var start = selected.Count;
            foreach (var turn in group)
                if (!Add(turn, kind))
                {
                    selected.RemoveRange(start, selected.Count - start);
                    return false;
                }

            return true;
        }

        // Keep the latest complete exchange, then durable context, then additional older history.
        if (groups.Length > 0) AddHistory(groups[^1], ContextItemKind.LatestHistory);

        void Reference(string label, string text, ContextItemKind kind, IReadOnlyList<Guid>? memories = null,
            IReadOnlyList<Guid>? messages = null, IReadOnlyList<ContentSource>? sources = null)
        {
            if (!string.IsNullOrWhiteSpace(text))
                Add(
                    new ContextTurn(MessageRole.User, $"[{label}: untrusted reference]\n{text}", MemoryIds: memories,
                        MessageIds: messages, Sources: sources), kind);
        }

        Reference("Rolling summary", input.Summary, ContextItemKind.Summary, input.SummaryMemoryIds,
            input.SummaryMessageIds,
            input.SummarySource is null ? [] : [input.SummarySource]);
        Reference($"Authorized relationship for {input.Current.SpeakerTag ?? "current speaker"}", input.Relationship,
            ContextItemKind.Relationship, sources: input.RelationshipSource is null ? [] : [input.RelationshipSource]);
        foreach (var memory in input.Memories)
        {
            var author = input.ConversationId is { } id ? SpeakerTags.For(id, memory.SubjectId) : "unknown";
            var name = input.AuthorNames?.GetValueOrDefault(memory.SubjectId);
            var label = $"Memory; author={author}" +
                        (string.IsNullOrWhiteSpace(name) ? "" : $"; preferredName={Json.ForPrompt(name)}");
            Reference(label, memory.Text, ContextItemKind.Memory, [memory.Id],
                sources: [ContentSources.Memory(memory)]);
        }

        foreach (var group in groups.SkipLast(1).Reverse())
            if (!AddHistory(group, ContextItemKind.OlderHistory))
                break;

        var ordered = selected.Where(x => !IsHistory(x.Kind))
            .Concat(selected.Where(x => IsHistory(x.Kind)).OrderBy(x => x.Turn.TurnSequence)).ToArray();
        var turns = ordered.Select(x => x.Turn).Append(input.Current).ToArray();
        var messages = new[] { systemMessage }.Concat(ordered.Select(x => x.Message)).Append(current).ToList();
        var optional = selected.OrderBy(x => RetentionWeight(x.Kind)).ThenBy(x => x.Turn.TurnSequence).GroupBy(x =>
                IsHistory(x.Kind)
                    ? $"history:{x.Turn.TurnSequence}"
                    : $"reference:{selected.IndexOf(x)}")
            .Select(x => x.Select(v => v.Message).ToArray()).ToArray();
        var fullHistory = input.History.Select(x => x.ToMessage(toolMode, false)).ToArray();
        var pressure = input.History.Count >= 25 ||
                       budget.Count(fullHistory) > budget.InputLimit * .55 ||
                       selected.Count(x => IsHistory(x.Kind)) < input.History.Count;
        return new BuiltContext(system, turns, turns.SelectMany(x => x.MemoryIds ?? []).Distinct().ToArray(),
            turns.SelectMany(x => x.MessageIds ?? []).Distinct().ToArray(),
            messages, optional, pressure, turns.SelectMany(x => x.Sources ?? []).Distinct().ToArray());
    }

    private static bool IsHistory(ContextItemKind kind)
    {
        return kind is ContextItemKind.OlderHistory or ContextItemKind.LatestHistory;
    }

    private static int RetentionWeight(ContextItemKind kind)
    {
        return kind switch
        {
            ContextItemKind.OlderHistory => 40,
            ContextItemKind.Memory => 60,
            ContextItemKind.Relationship => 70,
            ContextItemKind.LatestHistory => 90,
            ContextItemKind.Summary => 100,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}

public enum ContextItemKind
{
    OlderHistory = 0,
    Memory = 1,
    Relationship = 2,
    LatestHistory = 3,
    Summary = 4
}