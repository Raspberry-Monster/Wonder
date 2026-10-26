using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Infrastructure.Models;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host;

public sealed class ContextLimitException(string message) : Exception(message);

public sealed class OutputLimitException(string message) : ModelResponseException(message);

public sealed class TokenQuotaException(DateTimeOffset retryAt, string message) : Exception(message)
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}

public sealed class ModelContextBudget(TokenCounter tokens, ModelProfile profile, int? outputOverride = null)
{
    public const int Margin = 512;
    public int OutputTokens => profile.TaskKind == TaskKind.Embedding ? 0 : outputOverride ?? profile.MaxOutputTokens;

    public int InputLimit => profile.ContextWindow - OutputTokens - Margin;
    public int ToolReserve => Math.Min(4096, Math.Max(0, InputLimit / 3));
    public string Tokenizer => tokens.Resolve(profile);

    public ModelContextBudget WithOutput(int maximum)
    {
        return new ModelContextBudget(tokens, profile, maximum);
    }

    public int OutputAvailable(IEnumerable<AiMessage> messages, IList<AITool>? tools = null)
    {
        return profile.ContextWindow - Margin - Count(messages, tools);
    }

    public int Text(string value)
    {
        return tokens.Text(profile, value);
    }

    public int Count(IEnumerable<AiMessage> messages, IList<AITool>? tools = null)
    {
        return tokens.Messages(profile, messages, tools);
    }

    public int Remaining(IEnumerable<AiMessage> messages, IList<AITool>? tools = null, int reserved = 0)
    {
        return InputLimit - Count(messages, tools) - reserved;
    }

    public bool Fits(IEnumerable<AiMessage> messages, IList<AITool>? tools = null, int reserved = 0)
    {
        return Remaining(messages, tools, reserved) >= 0;
    }

    public int Require(IEnumerable<AiMessage> messages, IList<AITool>? tools = null)
    {
        var count = Count(messages, tools);
        if (count > InputLimit)
            throw new ContextLimitException($"Task={profile.TaskKind}; Input={count}; Available={InputLimit}; " +
                                            $"Output={OutputTokens}; Window={profile.ContextWindow}; Tokenizer={Tokenizer}. Required content does not fit.");
        return count;
    }

    public int RequireEmbedding(string text)
    {
        var count = Text(text);
        if (count > InputLimit)
            throw new ContextLimitException(
                $"Embedding input exceeds {InputLimit} tokens. Split the text into smaller facts.");
        return count;
    }

    public IEnumerable<string> Split(string text)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var length = PrefixLength(text[offset..], value => Text(value) <= InputLimit);
            if (length == 0) throw new ContextLimitException("The configured input limit cannot fit a text fragment.");
            yield return text.Substring(offset, length);
            offset += length;
        }
    }

    // The predicate measures the entire formatted request, including JSON and metadata.
    public static int PrefixLength(string text, Func<string, bool> fits)
    {
        if (fits(text)) return text.Length;
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var end = middle < text.Length && char.IsHighSurrogate(text[middle - 1]) ? middle - 1 : middle;
            if (end > 0 && fits(text[..end])) low = middle;
            else high = middle - 1;
        }

        if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
        if (low == 0) return 0;
        var paragraph = text.LastIndexOf('\n', low - 1, low);
        if (paragraph >= low / 2) low = paragraph + 1;
        // Token counts need not be monotonic. Return only a verified prefix.
        while (low > 0 && !fits(text[..low]))
        {
            low--;
            if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
        }

        return low;
    }
}