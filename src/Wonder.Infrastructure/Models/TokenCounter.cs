using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Tokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Infrastructure.Models;

// Tokenizers are immutable after loading. No lock surrounds requests or model calls.
public sealed record TokenizerDefinition(string Id, IReadOnlyList<string> Models);

public sealed class TokenCounter : IDisposable
{
    private readonly Dictionary<string, string> modelTokenizers;
    private readonly Dictionary<string, string> paths;
    private readonly ConcurrentDictionary<string, Lazy<Tokenizer>> tokenizers = new();

    public TokenCounter(string directory)
    {
        paths = (Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [])
            .Where(folder => File.Exists(Path.Combine(folder, "tokenizer.json")))
            .Order(StringComparer.Ordinal)
            .ToDictionary(folder => Path.GetFileName(folder), folder => Path.Combine(folder, "tokenizer.json"),
                StringComparer.OrdinalIgnoreCase);
        Available = paths.Select(entry =>
        {
            var aliases = Path.Combine(Path.GetDirectoryName(entry.Value)!, "models.json");
            return new TokenizerDefinition(entry.Key,
                File.Exists(aliases) ? Json.Read<string[]>(File.ReadAllText(aliases)) : []);
        }).ToArray();
        modelTokenizers = Available.SelectMany(item => item.Models.Prepend(item.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(model => (Model: model, item.Id)))
            .ToDictionary(item => item.Model, item => item.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<TokenizerDefinition> Available { get; }

    public void Dispose()
    {
        foreach (var item in tokenizers.Values.Where(x => x.IsValueCreated)) item.Value.Dispose();
    }

    public bool Contains(string id)
    {
        return id is "auto" or "estimate" || paths.ContainsKey(id);
    }

    public string Resolve(ModelProfile profile)
    {
        return profile.TokenizerId == "auto"
            ? modelTokenizers.GetValueOrDefault(profile.Model, "estimate")
            : profile.TokenizerId;
    }

    public static int Estimate(string text)
    {
        return Encoding.UTF8.GetByteCount(text);
    }

    public int Text(ModelProfile profile, string text)
    {
        var id = Resolve(profile);
        if (id == "estimate") return Estimate(text);
        return tokenizers.GetOrAdd(id, key => new Lazy<Tokenizer>(() => Load(key))).Value
            .Encode(text, false).First().Ids.Count;
    }

    private Tokenizer Load(string id)
    {
        if (!paths.TryGetValue(id, out var path))
            throw new ModelConfigurationException($"Tokenizer '{id}' was not found in the Tokenizer directory.");
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
                    FunctionCallContent call => Text(profile, call.Name + Json.ForPrompt(call.Arguments)) + 32,
                    FunctionResultContent result => Text(profile,
                        result.Result is string text ? text : Json.ForPrompt(result.Result)) + 32,
                    TextReasoningContent thought => Text(profile, thought.Text ?? ""),
                    DataContent or UriContent => profile.ImageTokenBudget,
                    _ => 0
                };
        }

        foreach (var tool in tools?.OfType<AIFunctionDeclaration>() ?? [])
            count += Text(profile, tool.JsonSchema.GetRawText() + tool.Description + tool.Name) + 128;
        return count;
    }
}