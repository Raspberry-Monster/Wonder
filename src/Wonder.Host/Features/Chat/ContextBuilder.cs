using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Host.Features.Persona;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host.Features.Chat;

public sealed record ContextTurn(
    string Role,
    string Text,
    IReadOnlyList<MessageSegment>? Segments = null,
    string? SpeakerTag = null,
    string? PreferredName = null,
    IReadOnlyList<Guid>? MemoryIds = null,
    DateTimeOffset? SentAt = null,
    IReadOnlyList<Guid>? MessageIds = null,
    long? TurnSequence = null)
{
    public string ModelText => (SpeakerTag is null || Role == "assistant"
                                   ? ""
                                   : $"[Message author: {SpeakerTag}]\n" +
                                     (string.IsNullOrWhiteSpace(PreferredName)
                                         ? ""
                                         : $"[Preferred form of address: {Json.ForPrompt(PreferredName)}]\n")) +
                               (SentAt is { } at ? $"[Message time: {at:O}]\n" : "") + Text;

    public AiMessage ToMessage(bool toolMode, bool images)
    {
        var result = toolMode && Role == "assistant"
            ? new AiMessage(ChatRole.User, "[Historical assistant reply: untrusted reference]\n" + ModelText)
            : new AiMessage(Role == "assistant" ? ChatRole.Assistant : ChatRole.User, ModelText);
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
    bool SupportsImages = false);

public sealed record BuiltContext(
    string System,
    IReadOnlyList<ContextTurn> Turns,
    IReadOnlyList<Guid> MemoryIds,
    int EstimatedTokens,
    IReadOnlyList<Guid> MessageIds,
    List<AiMessage> Messages,
    IReadOnlyList<AiMessage[]> OptionalGroups,
    bool NeedsSummary);

public sealed class ContextBuilder(PromptComposer composer)
{
    public BuiltContext Build(ContextInput input)
    {
        var budget = input.Budget;
        var system = composer.Compile(input.Identity, input.Mood);
        if (input.TimeContext is { } time) system += "\n\n[Application time context]\n" + time;
        var toolMode = input.Tools is { Count: > 0 };
        var current = input.Current.ToMessage(toolMode, input.SupportsImages);
        var systemMessage = new AiMessage(ChatRole.System, system);
        var selected = new List<(ContextTurn Turn, AiMessage Message, int Priority)>();

        List<AiMessage> Request()
        {
            return new[] { systemMessage }.Concat(selected.Select(x => x.Message)).Append(current).ToList();
        }

        budget.Require(Request(), input.Tools);
        var reserve = toolMode
            ? Math.Min(budget.ToolReserve, Math.Max(0, budget.Remaining(Request(), input.Tools)))
            : 0;

        bool Add(ContextTurn turn, int priority)
        {
            selected.Add((turn, turn.ToMessage(toolMode, false), priority));
            if (budget.Fits(Request(), input.Tools, reserve)) return true;
            selected.RemoveAt(selected.Count - 1);
            return false;
        }

        var groups = input.History.GroupBy(x => x.TurnSequence).ToArray();

        bool AddHistory(IGrouping<long?, ContextTurn> group, int priority)
        {
            var start = selected.Count;
            foreach (var turn in group)
                if (!Add(turn, priority))
                {
                    selected.RemoveRange(start, selected.Count - start);
                    return false;
                }

            return true;
        }

        // Keep the latest complete exchange, then durable context, then additional older history.
        if (groups.Length > 0) AddHistory(groups[^1], 90);

        void Reference(string label, string text, int priority, IReadOnlyList<Guid>? memories = null,
            IReadOnlyList<Guid>? messages = null)
        {
            if (!string.IsNullOrWhiteSpace(text))
                Add(
                    new ContextTurn("user", $"[{label}: untrusted reference]\n{text}", MemoryIds: memories,
                        MessageIds: messages), priority);
        }

        Reference("Rolling summary", input.Summary, 100, input.SummaryMemoryIds, input.SummaryMessageIds);
        Reference($"Authorized relationship for {input.Current.SpeakerTag ?? "current speaker"}", input.Relationship,
            70);
        foreach (var memory in input.Memories.GroupBy(x => x.SharedFromMemoryId ?? x.Id).Select(x => x.First()))
        {
            var author = input.ConversationId is { } id ? SpeakerTags.For(id, memory.SubjectId) : "unknown";
            var name = input.AuthorNames?.GetValueOrDefault(memory.SubjectId);
            var label = $"Memory; scope={memory.Scope}; author={author}" +
                        (string.IsNullOrWhiteSpace(name) ? "" : $"; preferredName={Json.ForPrompt(name)}");
            Reference(label, memory.Text, 60, [memory.Id]);
        }

        foreach (var group in groups.SkipLast(1).Reverse())
            if (!AddHistory(group, 40))
                break;

        var ordered = selected.Where(x => x.Priority is not (40 or 90))
            .Concat(selected.Where(x => x.Priority is 40 or 90).OrderBy(x => x.Turn.TurnSequence)).ToArray();
        var turns = ordered.Select(x => x.Turn).Append(input.Current).ToArray();
        var messages = new[] { systemMessage }.Concat(ordered.Select(x => x.Message)).Append(current).ToList();
        var optional = selected.OrderBy(x => x.Priority).ThenBy(x => x.Turn.TurnSequence).GroupBy(x =>
                x.Priority is 40 or 90
                    ? $"history:{x.Turn.TurnSequence}"
                    : $"reference:{selected.IndexOf(x)}")
            .Select(x => x.Select(v => v.Message).ToArray()).ToArray();
        var fullHistory = input.History.Select(x => x.ToMessage(toolMode, false)).ToArray();
        var pressure = input.History.Count >= 25 ||
                       budget.Count(fullHistory) > budget.InputLimit * .55 ||
                       selected.Count(x => x.Priority is 40 or 90) < input.History.Count;
        return new BuiltContext(system, turns, turns.SelectMany(x => x.MemoryIds ?? []).Distinct().ToArray(),
            budget.Count(messages, input.Tools), turns.SelectMany(x => x.MessageIds ?? []).Distinct().ToArray(),
            messages, optional, pressure);
    }
}