using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using EmbeddingClient = OpenAI.Embeddings.EmbeddingClient;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Infrastructure.Models;

public sealed class ProviderRetryException(TimeSpan retryAfter)
    : HttpRequestException("Provider requested delayed retry.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public sealed class ModelConfigurationException(string message) : Exception(message);
public sealed class ModelResponseException(string message, Exception? inner = null) : Exception(message, inner);

// The official adapter owns protocol serialization, images, responses and structured tool calls.
// This decorator only supplies provider options and preserves reasoning during tool round trips.
public sealed class CompatibleChatClient(HttpClient http, string model, bool supportsJson,
    bool? thinkingEnabled = null, string? reasoningEffort = null)
    : DelegatingChatClient(new ChatClient(model, CompatibleHttp.Credential(http), CompatibleHttp.Options(http)).AsIChatClient())
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<AiMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var configured = options?.Clone() ?? new ChatOptions();
        configured.MaxOutputTokens ??= 1024;
        if (!supportsJson) configured.ResponseFormat = null;
        var originalFactory = configured.RawRepresentationFactory;
        configured.RawRepresentationFactory = client =>
        {
            var raw = originalFactory?.Invoke(client) as ChatCompletionOptions ?? new();
#pragma warning disable SCME0001 // Official SDK extension point for compatible-provider options.
            raw.Patch.Set("$.max_tokens"u8, configured.MaxOutputTokens.Value);
            raw.Patch.Remove("$.max_completion_tokens"u8);
            if (thinkingEnabled.HasValue)
                raw.Patch.Set("$.thinking.type"u8, thinkingEnabled.Value ? "enabled" : "disabled");
            if (thinkingEnabled == true && reasoningEffort is not null)
                raw.Patch.Set("$.reasoning_effort"u8, reasoningEffort);
#pragma warning restore SCME0001
            return raw;
        };
        try
        {
            var response = await base.GetResponseAsync(messages, configured, cancellationToken);
            if (response.RawRepresentation is ChatCompletion completion && response.Messages.Count == 1)
            {
                var raw = new AssistantChatMessage(completion);
                var reasoning = string.Concat(response.Messages[0].Contents.OfType<TextReasoningContent>().Select(x => x.Text));
                if (reasoning.Length > 0)
                {
#pragma warning disable SCME0001
                    raw.Patch.Set("$.reasoning_content"u8, reasoning);
#pragma warning restore SCME0001
                }
                response.Messages[0].RawRepresentation = raw;
            }
            return response;
        }
        catch (ClientResultException ex) { throw CompatibleHttp.Translate(ex); }
        catch (JsonException ex) { throw new ModelResponseException("Provider returned an invalid or empty HTTP response for chat/completions.", ex); }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) http.Dispose();
    }
}

public sealed class CompatibleEmbeddingGenerator(HttpClient http, string model, int dimensions)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(
        new EmbeddingClient(model, CompatibleHttp.Credential(http), CompatibleHttp.Options(http)).AsIEmbeddingGenerator(dimensions))
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var configured = options?.Clone() ?? new EmbeddingGenerationOptions();
            configured.Dimensions = dimensions;
            var originalFactory = configured.RawRepresentationFactory;
            configured.RawRepresentationFactory = generator =>
            {
                var raw = originalFactory?.Invoke(generator) as OpenAI.Embeddings.EmbeddingGenerationOptions ?? new();
#pragma warning disable SCME0001
                raw.Patch.Set("$.encoding_format"u8, "float");
#pragma warning restore SCME0001
                return raw;
            };
            var result = await base.GenerateAsync(values, configured, cancellationToken);
            if (result.Any(x => x.Vector.Length != dimensions || x.Vector.ToArray().Any(v => !float.IsFinite(v))))
                throw new InvalidOperationException("Embedding dimension or values are invalid.");
            return result;
        }
        catch (ClientResultException ex) { throw CompatibleHttp.Translate(ex); }
        catch (JsonException ex) { throw new ModelResponseException("Provider returned an invalid or empty HTTP response for embeddings.", ex); }
    }
}

public static class CompatibleHttp
{
    public static HttpClient Configure(HttpClient http, string endpoint, string apiKey)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ModelConfigurationException("Invalid model endpoint.");
        http.BaseAddress = uri;
        http.Timeout = TimeSpan.FromSeconds(90);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }

    internal static ApiKeyCredential Credential(HttpClient http) => new(http.DefaultRequestHeaders.Authorization?.Parameter ?? "unused");
    internal static OpenAIClientOptions Options(HttpClient http) => new()
    {
        Endpoint = http.BaseAddress ?? throw new ModelConfigurationException("Model endpoint required."),
        Transport = new HttpClientPipelineTransport(http),
        RetryPolicy = new DurableRetryPolicy()
    };

    internal static Exception Translate(ClientResultException ex) => ex.Status is 400 or 401 or 403 or 404
        ? new ModelConfigurationException($"Provider rejected request ({ex.Status}).")
        : new HttpRequestException($"Provider request failed ({ex.Status}).", ex);

    // Extend the SDK retry policy solely to hand long waits to the persistent job queue.
    private sealed class DurableRetryPolicy() : ClientRetryPolicy(2)
    {
        protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
        {
            if (message.Response is { Status: 429 or >= 500 } response && response.Headers.TryGetValue("Retry-After", out var value))
            {
                var delay = double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
                if (delay > TimeSpan.FromSeconds(20)) throw new ProviderRetryException(delay);
            }
            return base.ShouldRetry(message, exception);
        }

        protected override ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) => new(ShouldRetry(message, exception));
    }
}
