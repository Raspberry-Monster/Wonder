using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.AI;

public sealed class ProviderRetryException(TimeSpan retryAfter)
    : HttpRequestException("Provider requested delayed retry.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public sealed class ModelConfigurationException(string message) : Exception(message);

public sealed class ModelResponseException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class CompatibleChatClient(
    HttpClient http, string model, bool supportsJson, bool? thinkingEnabled = null, string? reasoningEffort = null)
    : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<AiMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["max_tokens"] = options?.MaxOutputTokens ?? 1024,
            ["messages"] = messages.Select(m => new { role = m.Role.Value, content = MessageContent(m) }).ToArray()
        };
        if (thinkingEnabled.HasValue)
            body["thinking"] = new { type = thinkingEnabled.Value ? "enabled" : "disabled" };
        if (thinkingEnabled == true && reasoningEffort is not null)
            body["reasoning_effort"] = reasoningEffort;
        if (supportsJson && options?.ResponseFormat == ChatResponseFormat.Json)
            body["response_format"] = new { type = "json_object" };
        using var response = await CompatibleHttp.PostAsync(http, "chat/completions", body, cancellationToken);
        var e = response.RootElement;
        var choice = e.GetProperty("choices")[0];
        var text = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        var result = new ChatResponse(new AiMessage(ChatRole.Assistant, text))
        {
            ModelId = model,
            FinishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() is { } reason
                ? new ChatFinishReason(reason)
                : null
        };
        if (e.TryGetProperty("usage", out var usage))
            result.Usage = new UsageDetails
            {
                InputTokenCount = usage.TryGetProperty("prompt_tokens", out var i) ? i.GetInt64() : null,
                OutputTokenCount = usage.TryGetProperty("completion_tokens", out var o) ? o.GetInt64() : null
            };
        return result;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<AiMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private static object MessageContent(AiMessage message)
    {
        if (message.Contents.All(c => c is TextContent))
            return string.Concat(message.Contents.Cast<TextContent>().Select(c => c.Text));
        if (message.Role != ChatRole.User && message.Role != ChatRole.Tool)
            throw new NotSupportedException("Images are only supported in user or tool messages.");
        return message.Contents.Select(ToContent).ToArray();
    }

    private static object ToContent(AIContent content)
    {
        return content switch
        {
            TextContent t => new { type = "text", text = t.Text },
            DataContent d when d.MediaType.StartsWith("image/", StringComparison.Ordinal) => new
            {
                type = "image_url",
                image_url = new { url = $"data:{d.MediaType};base64,{Convert.ToBase64String(d.Data.Span)}" }
            },
            _ => throw new NotSupportedException("Unsupported model content.")
        };
    }
}

public sealed class CompatibleEmbeddingGenerator(HttpClient http, string model, int dimensions)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var doc = await CompatibleHttp.PostAsync(http, "embeddings",
            new { model, input = values.ToArray(), dimensions }, cancellationToken);
        var items = doc.RootElement.GetProperty("data").EnumerateArray().OrderBy(x => x.GetProperty("index").GetInt32())
            .Select(x =>
            {
                var vector = x.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                if (vector.Length != dimensions || vector.Any(v => !float.IsFinite(v)))
                    throw new InvalidOperationException("Embedding dimension or values are invalid.");
                return new Embedding<float>(vector) { ModelId = model };
            }).ToArray();
        return new GeneratedEmbeddings<Embedding<float>>(items);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}

public static class CompatibleHttp
{
    public static HttpClient Configure(HttpClient http, string endpoint, string apiKey)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ModelConfigurationException("Invalid model endpoint.");
        http.BaseAddress = uri;
        http.Timeout = TimeSpan.FromSeconds(90);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }

    public static async Task<JsonDocument> PostAsync(HttpClient http, string path, object body, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync(path, body, ct);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
                continue;
            }

            using (response)
            {
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) &&
                    attempt < 2)
                {
                    var wait = response.Headers.RetryAfter?.Delta ??
                               response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ??
                               TimeSpan.FromSeconds(1 << attempt);
                    // Long Retry-After is handed to the durable job retry rather than retrying early.
                    if (wait > TimeSpan.FromSeconds(20)) throw new ProviderRetryException(wait);
                    await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                if ((int)response.StatusCode is 400 or 401 or 403 or 404)
                    throw new ModelConfigurationException($"Provider rejected request ({(int)response.StatusCode}).");
                if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                    (response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) is
                    { } retryAfter && retryAfter > TimeSpan.Zero) throw new ProviderRetryException(retryAfter);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(content))
                    throw new ModelResponseException($"Provider returned an empty HTTP response for {path}.");
                return JsonDocument.Parse(content);
            }
        }
    }
}