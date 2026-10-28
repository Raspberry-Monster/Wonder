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
    ContentSource? RelationshipSource = null,
    RestoredReplay? Replay = null,
    bool BasicGroup = false);

public sealed record BuiltContext(
    string System,
    IReadOnlyList<ContextTurn> Turns,
    IReadOnlyList<Guid> MemoryIds,
    IReadOnlyList<Guid> MessageIds,
    List<AiMessage> Messages,
    IReadOnlyList<AiMessage[]> OptionalGroups,
    bool NeedsSummary,
    IReadOnlyList<ContentSource>? Sources = null,
    IReadOnlyList<ReplayGroup>? ReplayGroups = null,
    int CandidateTokens = 0,
    int HistoryTrimmed = 0,
    string? SummaryReason = null,
    bool CanSummarize = false);

public sealed class ContextBuilder(PromptComposer composer)
{
    public string System(ContextInput input)
    {
        if (input.BasicGroup)
        {
            var original = composer.Compile(input.Identity, input.Mood);
            if (input.Tools is not { Count: > 0 })
                original += "\nLive time and action tools are unavailable. Do not invent the current time or claim tool actions.";
            if (input.TimeContext is { } time) original += "\n\n[Application time context]\n" + time;
            return original;
        }
        return composer.CompileChat(input.Identity, input.Tools is { Count: > 0 });
    }

    public BuiltContext Build(ContextInput input)
    {
        var budget = input.Budget;
        var system = System(input);
        var toolMode = input.Tools is { Count: > 0 };
        var current = input.Current.ToMessage(toolMode, input.SupportsImages);
        var systemMessage = new AiMessage(ChatRole.System, system);
        if (input.BasicGroup)
        {
            List<AiMessage> groupRequest = [systemMessage, current];
            var count = budget.Require(groupRequest, input.Tools);
            return new BuiltContext(system, [input.Current], input.Current.MemoryIds ?? [],
                input.Current.MessageIds ?? [], groupRequest, [], false, input.Current.Sources, CandidateTokens: count);
        }
        var groups = new List<ReplayGroup>();
        var turns = new List<(ContextTurn Turn, AiMessage Message)>();
        var dynamic = new List<AiMessage>();

        void Reference(string label, string text, int weight, bool baseline = false,
            IReadOnlyList<Guid>? memories = null, IReadOnlyList<Guid>? messages = null,
            IReadOnlyList<ContentSource>? sources = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var turn = new ContextTurn(MessageRole.User, $"[{label}: untrusted reference]\n{text}",
                MemoryIds: memories, MessageIds: messages, Sources: sources);
            var msg = turn.ToMessage(toolMode, false);
            turns.Add((turn, msg));
            groups.Add(new ReplayGroup(null, [], [msg], weight));
            if (!baseline) dynamic.Add(msg);
        }

        if (input.Replay is { } replay)
            groups.AddRange(replay.Groups);
        else
        {
            Reference("Rolling summary", input.Summary, 100, true, input.SummaryMemoryIds,
                input.SummaryMessageIds, input.SummarySource is null ? [] : [input.SummarySource]);
            var history = input.History.GroupBy(x => x.TurnSequence).ToArray();
            foreach (var group in history)
            {
                var entries = group.Select(x => (Turn: x, Message: x.ToMessage(toolMode, false))).ToArray();
                turns.AddRange(entries);
                groups.Add(new ReplayGroup(group.Key, group.SelectMany(x => x.Sources?.Where(s => s.Kind == "Message").Select(s => s.Id) ?? x.MessageIds?.Take(1) ?? [])
                    .Distinct().ToArray(), entries.Select(x => x.Message).ToArray(),
                    group == history.Last() ? 90 : 40, group.Count()));
            }
        }

        // Mandatory state is always fresh, and the current user remains the final message for image loading.
        var state = new AiMessage(ChatRole.User, "[Application context: untrusted reference]\n" +
            "[Simulated expression]\n" + PromptComposer.Expression(input.Mood) +
            (input.TimeContext is { } time ? "\n[Application time context]\n" + time : ""));
        dynamic.Add(state);
        Reference($"Authorized relationship for {input.Current.SpeakerTag ?? "current speaker"}",
            input.Relationship, 70, sources: input.RelationshipSource is null ? [] : [input.RelationshipSource]);
        foreach (var memory in input.Memories)
        {
            var author = input.ConversationId is { } id ? SpeakerTags.For(id, memory.SubjectId) : "unknown";
            var name = input.AuthorNames?.GetValueOrDefault(memory.SubjectId);
            var label = $"Memory; author={author}" +
                (string.IsNullOrWhiteSpace(name) ? "" : $"; preferredName={Json.ForPrompt(name)}");
            Reference(label, memory.Text, 60, memories: [memory.Id], sources: [ContentSources.Memory(memory)]);
        }
        var historical = groups.Where(x => !x.Messages.Any(dynamic.Contains)).ToArray();
        var baselineMessages = historical.SelectMany(x => x.Messages).ToHashSet();
        List<AiMessage> Request() => new[] { systemMessage }
            .Concat(groups.SelectMany(x => x.Messages).Where(baselineMessages.Contains))
            .Concat(dynamic.Where(x => x == state || groups.Any(g => g.Messages.Contains(x))))
            .Append(current).ToList();

        budget.Require([systemMessage, state, current], input.Tools);
        var reserve = toolMode
            ? Math.Min(budget.ToolReserve, Math.Max(0, budget.Remaining([systemMessage, state, current], input.Tools)))
            : 0;
        var candidate = budget.Count(Request(), input.Tools);
        var trimmed = 0;
        var optional = groups.OrderBy(x => x.Weight).ThenBy(x => x.TurnSequence).ToArray();
        foreach (var group in optional)
        {
            if (groups.Sum(x => x.HistoryCount) <= 40 && budget.Fits(Request(), input.Tools, reserve)) break;
            if (groups.Sum(x => x.HistoryCount) > 40 && group.HistoryCount == 0) continue;
            groups.Remove(group);
            trimmed += group.HistoryCount;
        }
        var request = Request();
        budget.Require(request, input.Tools);
        var selectedTurns = turns.Where(x => request.Contains(x.Message)).Select(x => x.Turn)
            .Append(input.Current).ToArray();
        // Compression is useful only when there are complete exchanges outside the latest six turns.
        var complete = input.History.GroupBy(x => x.TurnSequence).OrderBy(x => x.Key)
            .SkipLast(6).Where(x => x.Any(t => t.Role == MessageRole.User) && x.Any(t => t.Role == MessageRole.Assistant)).ToArray();
        var compressible = complete.Length > 0;
        var reason = input.History.Count >= 38 ? "history_capacity" :
            candidate >= (budget.InputLimit - reserve) * .8 ? "input_pressure" :
            trimmed > 0 ? "history_trimmed" : null;
        var sources = selectedTurns.SelectMany(x => x.Sources ?? [])
            .Concat(input.Replay?.Sources ?? []).Distinct().ToArray();
        return new BuiltContext(system, selectedTurns,
            selectedTurns.SelectMany(x => x.MemoryIds ?? []).Concat(input.Replay?.MemoryIds ?? []).Distinct().ToArray(),
            selectedTurns.SelectMany(x => x.MessageIds ?? []).Concat(input.Replay?.MessageIds ?? []).Distinct().ToArray(),
            request, optional.Where(groups.Contains).Select(x => x.Messages).ToArray(),
            compressible && reason is not null, sources, historical.Where(groups.Contains).ToArray(),
            candidate, trimmed, compressible ? reason : null, compressible);
    }
}
