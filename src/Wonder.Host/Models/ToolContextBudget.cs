using Microsoft.Extensions.AI;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host;

// One instance per chat. FunctionInvokingChatClient executes tools sequentially.
public sealed class ToolContextBudget(
    ModelContextBudget budget,
    IList<AITool> tools,
    IReadOnlyList<AiMessage[]> optional)
{
    private readonly Dictionary<string, string> results = new();

    public void Prepare(List<AiMessage> messages)
    {
        Trim(messages, 0);
    }

    private void Trim(List<AiMessage> messages, int reserve)
    {
        foreach (var group in optional)
        {
            if (budget.Fits(messages, tools, reserve)) break;
            messages.RemoveAll(group.Contains);
        }
    }

    public Func<string, bool> ResultFits(IEnumerable<AiMessage> source, string callId)
    {
        var messages = source.ToList();
        var included = messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>().Select(x => x.CallId)
            .ToHashSet();
        foreach (var (id, result) in results.Where(x => !included.Contains(x.Key)))
            messages.Add(new AiMessage(ChatRole.Tool, [new FunctionResultContent(id, result)]));
        var pending = messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>()
            .Count(x => x.CallId != callId && !included.Contains(x.CallId) && !results.ContainsKey(x.CallId));
        return result =>
        {
            var candidate = messages.Append(new AiMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]))
                .ToList();
            Trim(candidate, 128 * pending);
            return budget.WithOutput(Math.Min(256, budget.OutputTokens)).Fits(candidate, tools, 128 * pending);
        };
    }

    public void Remember(string callId, string result)
    {
        results[callId] = result;
    }
}