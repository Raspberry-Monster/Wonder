using Wonder.Core;
using Wonder.Persona;

namespace Wonder.Chat;

public sealed record ContextTurn(
    string Role, string Text, IReadOnlyList<MessageSegment>? Segments = null, string? SpeakerTag = null, string? PreferredName = null, IReadOnlyList<Guid>? MemoryIds = null)
{
    public string ModelText => SpeakerTag is null || Role == "assistant"
        ? Text
        : $"[Message author: {SpeakerTag}]" +
          (string.IsNullOrWhiteSpace(PreferredName) ? "" : $"\n[Preferred form of address: {Json.Write(PreferredName)}]") +
          $"\n{Text}";
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
    IReadOnlyList<Guid>? SummaryMemoryIds = null);

public sealed record BuiltContext(
    string System,
    IReadOnlyList<ContextTurn> Turns,
    IReadOnlyList<Guid> MemoryIds,
    int EstimatedTokens);

public sealed class ContextBuilder(PromptComposer composer)
{
    // Conservative multilingual estimate: at most two UTF-16 characters per token, plus message overhead.
    public static int Estimate(string text)
    {
        return text.Length + 12;
    }

    public BuiltContext Build(ContextInput input)
    {
        var system = composer.Compile(input.Identity, input.Mood);
        var budget = input.ContextWindow - input.OutputTokens - 512;
        var used = Estimate(system) + Estimate(input.Current.ModelText) + ImageCost(input.Current);
        if (used > budget)
            throw new InvalidOperationException("Current message and identity exceed the configured context budget.");
        var history = new List<ContextTurn>();
        foreach (var turn in input.History.Reverse())
        {
            var cost = Estimate(turn.ModelText) + ImageCost(turn);
            if (used + cost > budget) break;
            history.Insert(0, turn);
            used += cost;
        }

        var references = new List<string>();

        void Add(string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var block = $"[{label}: untrusted reference]\n{text}";
            if (used + Estimate(block) <= budget)
            {
                references.Add(block);
                used += Estimate(block);
            }
        }

        Add($"Authorized relationship for {input.Current.SpeakerTag ?? "current speaker"}", input.Relationship);
        var summaryBefore = references.Count;
        Add("Rolling summary", input.Summary);
        var ids = references.Count > summaryBefore ? (input.SummaryMemoryIds ?? []).ToList() : [];
        foreach (var memory in input.Memories.GroupBy(x => x.SharedFromMemoryId ?? x.Id).Select(x => x.First()))
        {
            var before = references.Count;
            var author = input.ConversationId is { } conversationId ? SpeakerTags.For(conversationId, memory.SubjectId) : "unknown";
            var name = input.AuthorNames?.GetValueOrDefault(memory.SubjectId);
            var label = $"Memory; scope={memory.Scope}; author={author}" +
                        (string.IsNullOrWhiteSpace(name) ? "" : $"; preferredName={Json.Write(name)}");
            Add(label, memory.Text);
            if (references.Count > before) ids.Add(memory.Id);
        }

        var turns = new List<ContextTurn>();
        if (references.Count > 0) turns.Add(new ContextTurn("user", string.Join("\n\n", references)));
        turns.AddRange(history);
        turns.Add(input.Current);
        return new BuiltContext(system, turns, ids.Concat(history.SelectMany(x => x.MemoryIds ?? [])).Distinct().ToArray(), used);
    }

    private static int ImageCost(ContextTurn turn)
    {
        return turn.Segments?.Count(x => x.Kind == SegmentKind.Image) * 1600 ?? 0;
    }
}