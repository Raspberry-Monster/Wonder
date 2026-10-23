using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure.Models;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.UnitTests;

public sealed class ContextBudgetTests
{
    [Test]
    public async Task FragmentsFitTheFormattedRequestAndReassembleWithoutLosingUnicode()
    {
        using var counter = new TokenCounter(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var budget = new ContextBudget(counter).For(new ModelProfile
            { TokenizerId = "estimate", ContextWindow = 2048, MaxOutputTokens = 256 });
        var original = string.Concat(Enumerable.Repeat("中文😀 quoted \\\"\n", 800));

        AiMessage[] Request(IReadOnlyList<TextFragment<string>> parts)
        {
            return
            [
                new(ChatRole.System, new string('s', 200)),
                new(ChatRole.User, Json.ForPrompt(parts.Select(x => new { x.Text, x.Offset, x.Truncated })))
            ];
        }

        var offset = 0;
        var restored = "";
        var count = 0;
        while (offset < original.Length)
        {
            var batch = TextBatch.Select([original], x => x, offset, x => budget.Fits(Request(x)));
            await Assert.That(budget.Fits(Request(batch))).IsTrue();
            await Assert.That(char.IsHighSurrogate(batch[0].Text[^1])).IsFalse();
            restored += batch[0].Text;
            offset += batch[0].Text.Length;
            count++;
        }

        await Assert.That(restored).IsEqualTo(original);
        await Assert.That(count).IsGreaterThan(1);
    }

    [Test]
    public async Task SeveralToolResultsShareTheBudgetAndOnlyOptionalHistoryIsDropped()
    {
        using var counter = new TokenCounter(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var budget = new ContextBudget(counter).For(new ModelProfile
            { TokenizerId = "estimate", ContextWindow = 4096, MaxOutputTokens = 512 });
        var system = new AiMessage(ChatRole.System, "Required system instructions");
        var current = new AiMessage(ChatRole.User, "Required current question");
        var old = new AiMessage(ChatRole.User, new string('h', 1600));
        var calls = new AiMessage(ChatRole.Assistant,
            [new FunctionCallContent("one", "history"), new FunctionCallContent("two", "history")]);
        List<AiMessage> messages = [system, old, current, calls];
        var session = new ToolContextBudget(budget, [], [[old]]);
        var first = new string('a', 1800);
        await Assert.That(session.ResultFits(messages, "one")(first)).IsTrue();
        session.Remember("one", first);
        await Assert.That(session.ResultFits(messages, "two")(first)).IsFalse();
        var second = new string('b', 300);
        await Assert.That(session.ResultFits(messages, "two")(second)).IsTrue();
        messages.Add(new AiMessage(ChatRole.Tool,
            [new FunctionResultContent("one", first), new FunctionResultContent("two", second)]));
        session.Prepare(messages);
        await Assert.That(messages).Contains(system);
        await Assert.That(messages).Contains(current);
        await Assert.That(messages).Contains(calls);
        await Assert.That(messages).DoesNotContain(old);
        await Assert.That(budget.Fits(messages)).IsTrue();
    }
}