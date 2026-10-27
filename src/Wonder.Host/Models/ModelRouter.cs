using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host;

public sealed class ModelRouter(
    IDbContextFactory<WonderDbContext> factory,
    IHttpClientFactory http,
    SecretProtector secrets,
    TokenCounter tokens,
    TokenBudget budget,
    ILogger<ModelRouter> logger)
{
    public async Task<ModelProfile> ProfileAsync(Guid botId, TaskKind kind, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Models.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == botId && x.TaskKind == kind, ct) ??
               throw new ModelConfigurationException($"No route configured for {kind}.");
    }

    public async Task<ChatResponse> GenerateAsync(ModelProfile profile, IEnumerable<ChatMessage> messages, bool json,
        CancellationToken ct, IList<AITool>? tools = null, bool disableTools = false)
    {
        using var client = CreateTransport(profile);
        return await GenerateCoreAsync(client, profile, messages, json, ct, tools, disableTools);
    }

    public ModelContextBudget Context(ModelProfile profile)
    {
        return new ModelContextBudget(tokens, profile);
    }

    public IChatClient CreateToolClient(ModelProfile profile, Action<List<ChatMessage>>? prepare = null,
        Func<CancellationToken, Task>? beforeSend = null)
    {
        return new AccountedChatClient(CreateTransport(profile), this, profile, prepare, beforeSend);
    }

    private IChatClient CreateTransport(ModelProfile profile)
    {
        var clientHttp = CompatibleHttp.Configure(http.CreateClient("models"), profile.Endpoint,
            secrets.Unprotect(profile.ProtectedApiKey));
        return new CompatibleChatClient(clientHttp, profile.Model, profile.SupportsJson, profile.ThinkingEnabled,
            profile.ReasoningEffort);
    }

    private async Task<ChatResponse> GenerateCoreAsync(IChatClient client, ModelProfile profile,
        IEnumerable<ChatMessage> messages, bool json,
        CancellationToken ct, IList<AITool>? tools, bool disableTools, int? outputTokens = null)
    {
        var input = messages.ToArray();
        var context = outputTokens is { } output ? Context(profile).WithOutput(output) : Context(profile);
        var inputTokens = context.Require(input, tools);
        logger.LogInformation(
            "Model budget: Task={Task}, Model={Model}, Tokenizer={Tokenizer}, Input={Input}, Output={Output}, Remaining={Remaining}",
            profile.TaskKind, profile.Model, context.Tokenizer, inputTokens, context.OutputTokens,
            context.InputLimit - inputTokens);
        var reservation = await budget.ReserveAsync(profile, inputTokens + context.OutputTokens, ct);
        ChatResponse result;
        try
        {
            result = await client.GetResponseAsync(input,
                new ChatOptions
                {
                    MaxOutputTokens = context.OutputTokens,
                    ResponseFormat = json ? ChatResponseFormat.Json : ChatResponseFormat.Text,
                    Tools = tools,
                    ToolMode = disableTools ? ChatToolMode.None : ChatToolMode.Auto
                }, ct);
        }
        catch (Exception ex) when (ex is ModelConfigurationException or ProviderRetryException)
        {
            await budget.ReleaseAsync(reservation);
            throw;
        }
        catch
        {
            // A lost response can still have been billed. Keep an explicitly estimated charge.
            await budget.SettleAsync(reservation, profile, inputTokens, context.OutputTokens, CancellationToken.None,
                true);
            throw;
        }

        var i = result.Usage?.InputTokenCount ?? inputTokens;
        var o = result.Usage?.OutputTokenCount ?? context.Count(result.Messages);
        await budget.SettleAsync(reservation, profile, i, o, CancellationToken.None,
            result.Usage?.InputTokenCount is null || result.Usage?.OutputTokenCount is null);
        logger.LogInformation("Model usage: Task={Task}, Input={Input}, Output={Output}", profile.TaskKind, i, o);
        // Failed generations still consume tokens; record usage before rejecting the response.
        var details = $"Task={profile.TaskKind}; Model={profile.Model}; " +
                      $"FinishReason={result.FinishReason?.Value ?? "unknown"}; " +
                      $"MaxOutputTokens={context.OutputTokens}; OutputTokens={o}; " +
                      $"ThinkingEnabled={profile.ThinkingEnabled?.ToString() ?? "provider-default"}.";
        if (result.FinishReason == ChatFinishReason.Length)
            throw new OutputLimitException("Model response was truncated. " + details +
                                           " Increase the output limit or reduce input/thinking before retrying.");
        var calls = result.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>().Any();
        if (calls && (tools is not { Count: > 0 } || disableTools))
            throw new ModelResponseException("Unexpected, truncated or excessive tool calls. " + details);
        if (string.IsNullOrWhiteSpace(result.Text) && !calls)
            throw new ModelResponseException("Model returned no final content. " + details);
        return result;
    }

    public async Task<float[]> EmbedAsync(ModelProfile profile, string text, CancellationToken ct)
    {
        var inputTokens = Context(profile).RequireEmbedding(text);
        var reservation = await budget.ReserveAsync(profile, inputTokens, ct);
        using var client = CompatibleHttp.Configure(http.CreateClient("models"), profile.Endpoint,
            secrets.Unprotect(profile.ProtectedApiKey));
        using IEmbeddingGenerator<string, Embedding<float>> generator =
            new CompatibleEmbeddingGenerator(client, profile.Model, profile.EmbeddingDimensions);
        GeneratedEmbeddings<Embedding<float>> result;
        try
        {
            result = await generator.GenerateAsync([text], cancellationToken: ct);
        }
        catch (Exception ex) when (ex is ModelConfigurationException or ProviderRetryException)
        {
            await budget.ReleaseAsync(reservation);
            throw;
        }
        catch
        {
            await budget.SettleAsync(reservation, profile, inputTokens, 0, CancellationToken.None, true);
            throw;
        }

        await budget.SettleAsync(reservation, profile, result.Usage?.InputTokenCount ?? inputTokens, 0,
            CancellationToken.None,
            result.Usage?.InputTokenCount is null);
        return result[0].Vector.ToArray();
    }

    // Accounting and limits are application policy; function dispatch and looping belong to M.E.AI.
    private sealed class AccountedChatClient(
        IChatClient inner,
        ModelRouter router,
        ModelProfile profile,
        Action<List<ChatMessage>>? prepare,
        Func<CancellationToken, Task>? beforeSend) : DelegatingChatClient(inner)
    {
        private int calls;
        private int rounds;

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var disabled = rounds++ >= 3 || calls >= 8 || options?.ToolMode == ChatToolMode.None;
            var input = messages.ToList();
            prepare?.Invoke(input);
            if (beforeSend is not null) await beforeSend(cancellationToken);
            var available = router.Context(profile).OutputAvailable(input, options?.Tools);
            var output = Math.Min(profile.MaxOutputTokens, available);
            if (output < Math.Min(256, profile.MaxOutputTokens))
                throw new ContextLimitException("The required tool conversation leaves no room for a final answer.");
            var result = await router.GenerateCoreAsync(InnerClient, profile, input, false, cancellationToken,
                options?.Tools, disabled, output);
            var count = result.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>().Count();
            if (count > 8 - calls) throw new ModelResponseException("Model exceeded the tool call limit.");
            calls += count;
            return result;
        }
    }
}
