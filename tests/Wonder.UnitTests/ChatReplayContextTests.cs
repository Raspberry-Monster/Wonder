using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Host;
using Wonder.Host.Features.Chat;
using Wonder.Host.Features.Persona;
using Wonder.Infrastructure.Models;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.UnitTests;

public sealed class ChatReplayContextTests
{
    [Test]
    public async Task LayoutKeepsSummaryBeforeHistoryAndFreshReferencesAfterHistory()
    {
        using var tokens = new TokenCounter("missing-tokenizers");
        var builder = Builder();
        var input = Input(tokens, 65536, 2) with
        {
            Summary = "summary", Relationship = "relationship", Memories = [new Memory { Text = "memory" }],
            TimeContext = "fresh time"
        };
        var context = builder.Build(input);
        var text = context.Messages.Select(x => x.Text).ToArray();
        await Assert.That(text[0]).DoesNotContain("fresh time");
        await Assert.That(text[1]).Contains("Rolling summary");
        await Assert.That(text[2]).Contains("old user 0");
        await Assert.That(text[6]).Contains("fresh time");
        await Assert.That(text[7]).Contains("relationship");
        await Assert.That(text[8]).Contains("memory");
        await Assert.That(text[^1]).IsEqualTo("current");
        await Assert.That(builder.Build(input with { Mood = new GlobalEmotionState { Valence = -.9 },
            TimeContext = "different time" }).System).IsEqualTo(context.System);
    }

    [Test]
    [Arguments(12, false)]
    [Arguments(19, true)]
    public async Task HistoryCapacityUsesThirtyEightMessagesInsteadOfTwentyFive(int rounds, bool expected)
    {
        using var tokens = new TokenCounter("missing-tokenizers");
        var context = Builder().Build(Input(tokens, 65536, rounds));
        await Assert.That(context.NeedsSummary).IsEqualTo(expected);
        if (expected) await Assert.That(context.SummaryReason).IsEqualTo("history_capacity");
    }

    [Test]
    public async Task FullCandidatePressureIncludesOptionalReferencesButNeedsCompressibleRounds()
    {
        using var tokens = new TokenCounter("missing-tokenizers");
        var input = Input(tokens, 8192, 7) with { Memories = [new Memory { Text = new string('m', 12000) }] };
        var context = Builder().Build(input);
        await Assert.That(context.NeedsSummary).IsTrue();
        await Assert.That(context.SummaryReason).IsEqualTo("input_pressure");
        await Assert.That(context.Messages.Any(x => x.Text.Contains(new string('m', 100)))).IsFalse();
        await Assert.That(Builder().Build(input with { History = input.History.Take(12).ToArray() }).NeedsSummary).IsFalse();
    }

    [Test]
    public async Task ReplayTrimmingDropsWholeToolRoundsAndCountsOnlyDatabaseHistory()
    {
        using var tokens = new TokenCounter("missing-tokenizers");
        var input = Input(tokens, 8192, 7);
        AiMessage[] old = [new(ChatRole.User, new string('x', 10000)),
            new(ChatRole.Assistant, [new FunctionCallContent("old", "get_current_time")]),
            new(ChatRole.Tool, [new FunctionResultContent("old", "result")]), new(ChatRole.Assistant, "old answer")];
        AiMessage[] recent = [new(ChatRole.User, "recent user"), new(ChatRole.Assistant, "recent answer")];
        var context = Builder().Build(input with { Replay = new RestoredReplay(
            [new(1, [], old, 40, 2), new(2, [], recent, 90, 2)], [], [], [], []) });
        await Assert.That(context.HistoryTrimmed).IsEqualTo(2);
        await Assert.That(context.Messages.Intersect(old)).IsEmpty();
        await Assert.That(context.Messages.Intersect(recent).Count()).IsEqualTo(2);
        await Assert.That(context.Messages.Last().Text).IsEqualTo("current");
    }

    [Test]
    public async Task UnknownContentOrIncompleteToolGroupsCannotBecomeReplay()
    {
        await Assert.That(() => ReplayMessages.Encode(new AiMessage(ChatRole.User,
                [new UriContent("https://example.com/image.png", "image/png")]), _ => null))
            .ThrowsExactly<NotSupportedException>();
        var payload = new ReplayPayload(1,
            [new("system", [new("text", "system")]), new("assistant", [new("call", CallId: "unfinished", Name: "tool")])],
            [new(1, 1, 1, [], 40, 2)], [], [], [], []);
        await Assert.That(() => ReplayMessages.ValidateGroups(payload)).ThrowsExactly<NotSupportedException>();
    }

    private static ContextBuilder Builder() => new(new PromptComposer(new PromptCatalog(Path.Combine(AppContext.BaseDirectory, "Prompts"))));
    private static ContextInput Input(TokenCounter tokens, int window, int rounds) => new("Wonder", new(), "", "", [],
        Enumerable.Range(0, rounds).SelectMany(i => new[]
        {
            new ContextTurn(MessageRole.User, "old user " + i, TurnSequence: i + 1),
            new ContextTurn(MessageRole.Assistant, "old assistant " + i, TurnSequence: i + 1)
        }).ToArray(), new(MessageRole.User, "current"),
        new ModelContextBudget(tokens, new ModelProfile { TokenizerId = "estimate", ContextWindow = window, MaxOutputTokens = 1024 }));
}
