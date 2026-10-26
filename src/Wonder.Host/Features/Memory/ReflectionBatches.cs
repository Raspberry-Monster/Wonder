using Wonder.Core;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using StoredMessage = Wonder.Core.ChatMessage;

namespace Wonder.Host;

public sealed class ReflectionProgress
{
    public long Sequence { get; set; }
    public int Offset { get; set; }
    public Dictionary<Guid, string> SourceHashes { get; set; } = [];
    public List<EmotionProposal> Emotions { get; set; } = [];
    public ReflectionProposal Proposal { get; set; } = new(false, null, [], []);
}

public static class ReflectionBatches
{
    public static ReflectionProposal Merge(IEnumerable<ReflectionProposal> proposals)
    {
        var all = proposals.ToArray();
        var emotions = all.Select(x => x.Emotion).OfType<EmotionProposal>().ToArray();
        var emotion = emotions.Length == 0
            ? null
            : new EmotionProposal(
                emotions.Average(x => x.Valence), emotions.Average(x => x.Arousal), emotions.Average(x => x.Energy), "",
                emotions.SelectMany(x => x.EvidenceMessageIds).Distinct().ToArray());
        return new ReflectionProposal(false, emotion,
            all.SelectMany(x => x.Relationships).GroupBy(x => (x.Preference, x.Explicit, x.Kind))
                .Select(g => g.First() with
                {
                    EvidenceMessageIds = g.SelectMany(x => x.EvidenceMessageIds).Distinct().ToArray(),
                    Assertions = MergeAssertions(g.Select(x => x.Assertions))
                }).ToArray(),
            all.SelectMany(x => x.Memories).GroupBy(x => (x.FactKey, x.Text, x.Explicit, x.Sensitive))
                .Select(g => g.First() with
                {
                    EvidenceMessageIds = g.SelectMany(x => x.EvidenceMessageIds).Distinct().ToArray(),
                    Assertions = MergeAssertions(g.Select(x => x.Assertions))
                }).ToArray());
    }

    private static EvidenceAssertion[]? MergeAssertions(IEnumerable<EvidenceAssertion[]?> groups)
    {
        var rows = groups.ToArray();
        // Missing assertions cannot supply an anchor for an evidence ID.
        return rows.All(x => x is null) ? null : rows.SelectMany(x => x ?? []).Distinct().ToArray();
    }

    public static async Task<(List<TextFragment<StoredMessage>> Batch, ReflectionProposal Proposal)> GenerateAsync(
        ModelRouter models, ModelProfile profile, IReadOnlyList<StoredMessage> rows, int offset,
        Func<IReadOnlyList<TextFragment<StoredMessage>>, AiMessage[]> request, Func<string, ReflectionProposal> parse,
        CancellationToken ct)
    {
        var budget = models.Context(profile);
        var limit = int.MaxValue;
        for (var attempt = 0;; attempt++)
        {
            var batch = TextBatch.Select(rows, x => x.Text, offset, values => budget.Fits(request(values)), limit);
            try
            {
                return (batch, parse((await models.GenerateAsync(profile, request(batch), true, ct)).Text));
            }
            catch (OutputLimitException) when (attempt < 2 && batch.Sum(x => x.Text.Length) > 32)
            {
                limit = Math.Max(1, batch.Sum(x => x.Text.Length) / 2);
            }
        }
    }
}