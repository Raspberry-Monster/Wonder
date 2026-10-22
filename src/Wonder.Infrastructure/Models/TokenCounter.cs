using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Wonder.Core;
using Tokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Infrastructure.Models;

// Tokenizers are immutable after loading. No lock surrounds requests or model calls.
public sealed class TokenCounter(IConfiguration configuration) : IDisposable
{
    public const string DeepSeekId = "deepseek-v4.1-flash-1017";
    private readonly ConcurrentDictionary<string, Lazy<Tokenizer>> tokenizers = new();

    public static int Estimate(string text) => Encoding.UTF8.GetByteCount(text);

    public int Text(ModelProfile profile, string text)
    {
        if (profile.TokenizerId == "estimate") return Estimate(text);
        return tokenizers.GetOrAdd(profile.TokenizerId, id => new Lazy<Tokenizer>(() => Load(id))).Value
            .Encode(text, false).First().Ids.Count;
    }

    private Tokenizer Load(string id)
    {
        var bundled = id == DeepSeekId;
        var path = bundled
            ? Path.Combine(AppContext.BaseDirectory, "Tokenizers", DeepSeekId, "tokenizer.json")
            : configuration[$"Tokenizers:{id}:Path"];
        var expected = bundled ? "C90DFA01249DB1BE4245780A052EDE752E1361C612AC6D08E2BDADA7D599476B"
            : configuration[$"Tokenizers:{id}:Sha256"];
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(expected))
            throw new ModelConfigurationException($"Tokenizer '{id}' requires a server-side Path and Sha256.");
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ModelConfigurationException($"Tokenizer '{id}' checksum does not match.");
        return Tokenizer.FromFile(path);
    }

    public int Messages(ModelProfile profile, IEnumerable<AiMessage> messages, IList<AITool>? tools = null)
    {
        // Text uses the selected vocabulary. Provider framing remains an explicit budget estimate.
        var count = 32;
        foreach (var message in messages)
        {
            count += profile.MessageTokenOverhead + Text(profile, message.Text);
            if (message.AdditionalProperties?.TryGetValue("reasoning_content", out var reasoning) == true)
                count += Text(profile, reasoning?.ToString() ?? "");
            foreach (var content in message.Contents)
                count += content switch
                {
                    FunctionCallContent call => Text(profile, call.Name + Json.Write(call.Arguments)) + 32,
                    FunctionResultContent result => Text(profile, result.Result is string text ? text : Json.Write(result.Result)) + 32,
                    TextReasoningContent thought => Text(profile, thought.Text ?? ""),
                    DataContent or UriContent => profile.ImageTokenBudget,
                    _ => 0
                };
        }
        foreach (var tool in tools?.OfType<AIFunctionDeclaration>() ?? [])
            count += Text(profile, tool.JsonSchema.GetRawText() + tool.Description + tool.Name) + 128;
        return count;
    }

    public void Dispose()
    {
        foreach (var item in tokenizers.Values.Where(x => x.IsValueCreated)) item.Value.Dispose();
    }
}
