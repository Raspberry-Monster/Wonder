using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Wonder.Host;

// Diagnostics only: never used for quotas or billing, and never inferred from local prefix similarity.
public static class ChatCacheUsage
{
    public static (long? Cached, long? Uncached) Read(ChatResponse response)
    {
        long? cached = response.Usage?.CachedInputTokenCount;
        long? uncached = null;
        if (response.RawRepresentation is ChatCompletion { Usage: { } usage })
        {
            try
            {
                using var data = JsonDocument.Parse(ModelReaderWriter.Write(usage, new ModelReaderWriterOptions("J")));
                long? Count(string name) => data.RootElement.TryGetProperty(name, out var value) &&
                    value.TryGetInt64(out var count) && count >= 0 ? count : null;
                cached = Count("prompt_cache_hit_tokens") ?? cached;
                uncached = Count("prompt_cache_miss_tokens");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                // A provider's optional usage extensions must not turn a successful chat into a failure.
            }
        }
        uncached ??= response.Usage?.InputTokenCount is { } total && cached is { } hit && total >= hit ? total - hit : null;
        return (cached, uncached);
    }
}
