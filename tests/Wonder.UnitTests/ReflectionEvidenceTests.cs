using Wonder.Core;
using Wonder.Host;

namespace Wonder.UnitTests;

public sealed class ReflectionEvidenceTests
{
    [Test]
    public async Task BatchMergePreservesEveryAssertionAndKeepsBoundaryDistinctFromPreference()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var a = new EvidenceAssertion(first, "CurrentStatement", 0, 2, "当前");
        var b = new EvidenceAssertion(second, "CurrentConfirmation", 4, 2, "仍然", 4, "仍然");
        var merged = ReflectionBatches.Merge([
            new ReflectionProposal(false, null, [new RelationshipProposal("简短", true, [first], "Preference", [a])],
                [new MemoryCandidate("color", "blue", true, false, [first], [a])]),
            new ReflectionProposal(false, null, [new RelationshipProposal("简短", true, [second], "Preference", [b]),
                new RelationshipProposal("简短", true, [second], "Boundary", [b])],
                [new MemoryCandidate("color", "blue", true, false, [second], [b])])]);
        await Assert.That(merged.Memories.Single().EvidenceMessageIds).IsEquivalentTo(new[] { first, second });
        await Assert.That(merged.Memories.Single().Assertions!).IsEquivalentTo(new[] { a, b });
        await Assert.That(merged.Relationships.Select(x => x.Kind)).IsEquivalentTo(new[] { "Preference", "Boundary" });
        await Assert.That(merged.Relationships.Single(x => x.Kind == "Preference").Assertions!).IsEquivalentTo(new[] { a, b });
        var legacy = ReflectionBatches.Merge([new ReflectionProposal(false, null, [], [new MemoryCandidate("key", "body", true, false, [first])])]);
        await Assert.That(legacy.Memories.Single().Assertions).IsNull();
    }
}
