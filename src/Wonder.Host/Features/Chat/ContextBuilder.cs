using Wonder.Core;
using Wonder.Host.Features.Persona;

namespace Wonder.Host.Features.Chat;

public sealed record ContextTurn(
    string Role, string Text, IReadOnlyList<MessageSegment>? Segments = null, string? SpeakerTag = null, string? PreferredName = null, IReadOnlyList<Guid>? MemoryIds = null, DateTimeOffset? SentAt = null, IReadOnlyList<Guid>? MessageIds = null)
{
    public string ModelText => (SpeakerTag is null || Role == "assistant" ? "" :
        $"[Message author: {SpeakerTag}]\n" +
        (string.IsNullOrWhiteSpace(PreferredName) ? "" : $"[Preferred form of address: {Json.ForPrompt(PreferredName)}]\n")) +
        (SentAt is { } at ? $"[Message time: {at:O}]\n" : "") + Text;
}

public sealed record ContextInput(
    string Identity,
    GlobalEmotionState Mood,
    string Relationship,
    string Summary,
    IReadOnlyList<Core.Memory> Memories,
    IReadOnlyList<ContextTurn> History,
    ContextTurn Current,
    int ContextWindow,
    int OutputTokens,
    Guid? ConversationId = null,
    IReadOnlyDictionary<Guid, string>? AuthorNames = null,
    IReadOnlyList<Guid>? SummaryMemoryIds = null, string? TimeContext = null, IReadOnlyList<Guid>? SummaryMessageIds = null, int ReservedForTools = 0, Func<string, int>? CountTokens = null, int ImageTokenBudget = 4096);

public sealed record BuiltContext(
    string System,
    IReadOnlyList<ContextTurn> Turns,
    IReadOnlyList<Guid> MemoryIds,
    int EstimatedTokens,
    IReadOnlyList<Guid> MessageIds);

public sealed class ContextBuilder(PromptComposer composer)
{
    // Explicit fallback for callers without a model profile.
    public static int Estimate(string text)
    {
        return Wonder.Infrastructure.Models.TokenCounter.Estimate(text) + 16;
    }

    public BuiltContext Build(ContextInput input)
    {
        int Count(string text) => input.CountTokens is { } count ? count(text) + 16 : Estimate(text);
        var system = composer.Compile(input.Identity, input.Mood);
        if (input.TimeContext is { } time) system += "\n\n[Application time context]\n" + time;
        var budget = input.ContextWindow - input.OutputTokens - 512;
        var used = Count(system) + Count(input.Current.ModelText) + ImageCost(input.Current, input.ImageTokenBudget);
        if (used > budget)
            throw new InvalidOperationException("Current message and identity exceed the configured context budget.");
        budget -= Math.Min(input.ReservedForTools, budget - used);
        var history = new List<ContextTurn>();
        foreach (var turn in input.History.Reverse())
        {
            var cost = Count(turn.ModelText) + ImageCost(turn, input.ImageTokenBudget);
            if (used + cost > budget) break;
            history.Insert(0, turn);
            used += cost;
        }

        var references = new List<string>();

        void Add(string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var block = $"[{label}: untrusted reference]\n{text}";
            if (used + Count(block) <= budget)
            {
                references.Add(block);
                used += Count(block);
            }
        }

        Add($"Authorized relationship for {input.Current.SpeakerTag ?? "current speaker"}", input.Relationship);
        var summaryBefore = references.Count;
        Add("Rolling summary", input.Summary);
        var ids = references.Count > summaryBefore ? (input.SummaryMemoryIds ?? []).ToList() : [];
        var messageIds = references.Count > summaryBefore ? (input.SummaryMessageIds ?? []).ToList() : [];
        foreach (var memory in input.Memories.GroupBy(x => x.SharedFromMemoryId ?? x.Id).Select(x => x.First()))
        {
            var before = references.Count;
            var author = input.ConversationId is { } conversationId ? SpeakerTags.For(conversationId, memory.SubjectId) : "unknown";
            var name = input.AuthorNames?.GetValueOrDefault(memory.SubjectId);
            var label = $"Memory; scope={memory.Scope}; author={author}" +
                        (string.IsNullOrWhiteSpace(name) ? "" : $"; preferredName={Json.ForPrompt(name)}");
            Add(label, memory.Text);
            if (references.Count > before) ids.Add(memory.Id);
        }

        var turns = new List<ContextTurn>();
        if (references.Count > 0) turns.Add(new ContextTurn("user", string.Join("\n\n", references)));
        turns.AddRange(history);
        turns.Add(input.Current);
        return new BuiltContext(system, turns, ids.Concat(history.SelectMany(x => x.MemoryIds ?? [])).Distinct().ToArray(), used,
            messageIds.Concat(history.SelectMany(x => x.MessageIds ?? [])).Concat(input.Current.MessageIds ?? []).Distinct().ToArray());
    }

    private static int ImageCost(ContextTurn turn, int budget)
    {
        return turn.Segments?.Count(x => x.Kind == SegmentKind.Image) * budget ?? 0;
    }
}
