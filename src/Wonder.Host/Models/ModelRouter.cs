using Wonder.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Infrastructure.Models;
using Wonder.Core;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host;

public sealed class ModelRouter(
    IDbContextFactory<WonderDbContext> factory,
    IHttpClientFactory http,
    SecretProtector secrets,
    TokenCounter tokens,
    TokenBudget budget)
{
    public async Task<ModelProfile> ProfileAsync(Guid botId, TaskKind kind, CancellationToken ct)
    {
        return await ReadProfileAsync(botId, kind, ct) ??
               throw new ModelConfigurationException($"No route configured for {kind}.");
    }

    private async Task<ModelProfile?> ReadProfileAsync(Guid botId, TaskKind kind, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Models.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == botId && x.TaskKind == kind, ct);
    }

    public async Task<ChatResponse> GenerateAsync(ModelProfile profile, IEnumerable<ChatMessage> messages, bool json,
        CancellationToken ct, IList<AITool>? tools = null, bool disableTools = false)
    {
        using var client = CreateTransport(profile);
        return await GenerateCoreAsync(client, profile, messages, json, ct, tools, disableTools);
    }

    public IChatClient CreateToolClient(ModelProfile profile) => new AccountedChatClient(CreateTransport(profile), this, profile);

    private IChatClient CreateTransport(ModelProfile profile)
    {
        var clientHttp = CompatibleHttp.Configure(http.CreateClient("models"), profile.Endpoint,
            secrets.Unprotect(profile.ProtectedApiKey));
        return new CompatibleChatClient(clientHttp, profile.Model, profile.SupportsJson, profile.ThinkingEnabled, profile.ReasoningEffort);
    }

    private async Task<ChatResponse> GenerateCoreAsync(IChatClient client, ModelProfile profile, IEnumerable<ChatMessage> messages, bool json,
        CancellationToken ct, IList<AITool>? tools, bool disableTools)
    {
        var input = messages.ToArray();
        var inputTokens = CountInput(profile, input, tools);
        if (inputTokens + profile.MaxOutputTokens + 512 > profile.ContextWindow)
            throw new ModelResponseException("Conversation exceeds the configured context window.");
        var reservation = await budget.ReserveAsync(profile, inputTokens + profile.MaxOutputTokens, ct);
        ChatResponse result;
        try
        {
        result = await client.GetResponseAsync(input,
            new ChatOptions
            {
                MaxOutputTokens = profile.MaxOutputTokens,
                ResponseFormat = json ? ChatResponseFormat.Json : ChatResponseFormat.Text,
                Tools = tools,
                ToolMode = disableTools ? ChatToolMode.None : ChatToolMode.Auto
            }, ct);
        }
        catch { await budget.ReleaseAsync(reservation); throw; }
        var i = result.Usage?.InputTokenCount ?? inputTokens;
        var o = result.Usage?.OutputTokenCount ?? CountInput(profile, result.Messages, null);
        await budget.SettleAsync(reservation, profile, i, o, CancellationToken.None);
        // Failed generations still consume tokens; record usage before rejecting the response.
        var details = $"Task={profile.TaskKind}; Model={profile.Model}; " +
                      $"FinishReason={result.FinishReason?.Value ?? "unknown"}; " +
                      $"MaxOutputTokens={profile.MaxOutputTokens}; OutputTokens={o}; " +
                      $"ThinkingEnabled={profile.ThinkingEnabled?.ToString() ?? "provider-default"}.";
        if (json && result.FinishReason == ChatFinishReason.Length)
            throw new ModelResponseException("Structured model response was truncated. " + details +
                " Increase the output limit or reduce input/thinking before retrying.");
        if (ToolCallMarkup.Contains(result.Text))
            throw new ModelResponseException("Model returned unparsed or ambiguous tool call markup instead of a final answer. " + details);
        var calls = result.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>().Any();
        if (calls && (tools is not { Count: > 0 } || disableTools || result.FinishReason == ChatFinishReason.Length))
            throw new ModelResponseException("Unexpected, truncated or excessive tool calls. " + details);
        if (string.IsNullOrWhiteSpace(result.Text) && !calls)
            throw new ModelResponseException("Model returned no final content. " + details);
        return result;
    }

    public int CountText(ModelProfile profile, string text) => tokens.Text(profile, text);
    public int CountInput(ModelProfile profile, IEnumerable<ChatMessage> messages, IList<AITool>? tools) => tokens.Messages(profile, messages, tools);

    // Accounting and limits are application policy; function dispatch and looping belong to M.E.AI.
    private sealed class AccountedChatClient(IChatClient inner, ModelRouter router, ModelProfile profile) : DelegatingChatClient(inner)
    {
        private int rounds;
        private int calls;
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var disabled = rounds++ >= 3 || calls >= 8 || options?.ToolMode == ChatToolMode.None;
            var result = await router.GenerateCoreAsync(InnerClient, profile, messages, false, cancellationToken, options?.Tools, disabled);
            var count = result.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>().Count();
            if (count > 8 - calls) throw new ModelResponseException("Model exceeded the tool call limit.");
            calls += count;
            return result;
        }
    }

    public async Task<float[]> EmbedAsync(ModelProfile profile, string text, CancellationToken ct)
    {
        var reservation = await budget.ReserveAsync(profile, CountText(profile, text), ct);
        using var client = CompatibleHttp.Configure(http.CreateClient("models"), profile.Endpoint,
            secrets.Unprotect(profile.ProtectedApiKey));
        using IEmbeddingGenerator<string, Embedding<float>> generator =
            new CompatibleEmbeddingGenerator(client, profile.Model, profile.EmbeddingDimensions);
        GeneratedEmbeddings<Embedding<float>> result;
        try { result = await generator.GenerateAsync([text], cancellationToken: ct); }
        catch { await budget.ReleaseAsync(reservation); throw; }
        await budget.SettleAsync(reservation, profile, result.Usage?.InputTokenCount ?? CountText(profile, text), 0, CancellationToken.None);
        return result[0].Vector.ToArray();
    }
}
