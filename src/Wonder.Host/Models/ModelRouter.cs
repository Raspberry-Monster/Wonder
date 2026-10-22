using Wonder.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Infrastructure.Models;
using Wonder.Core;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host;

public sealed class ModelRouter(
    IDbContextFactory<WonderDbContext> factory,
    IHttpClientFactory http,
    SecretProtector secrets)
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
        if (tools is { Count: > 0 } && EstimateInput(input, tools) + profile.MaxOutputTokens + 512 > profile.ContextWindow)
            throw new ModelResponseException("Tool conversation exceeds the configured context window.");
        await CheckBudgetAsync(profile, EstimateInput(input, tools) + profile.MaxOutputTokens, ct);
        var result = await client.GetResponseAsync(input,
            new ChatOptions
            {
                MaxOutputTokens = profile.MaxOutputTokens,
                ResponseFormat = json ? ChatResponseFormat.Json : ChatResponseFormat.Text,
                Tools = tools,
                ToolMode = disableTools ? ChatToolMode.None : ChatToolMode.Auto
            }, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var i = result.Usage?.InputTokenCount ?? EstimateInput(input, tools);
        var o = result.Usage?.OutputTokenCount ?? EstimateInput(result.Messages, null);
        db.Usage.Add(new UsageRecord
        {
            BotId = profile.BotId,
            ModelProfileId = profile.Id,
            TaskKind = profile.TaskKind,
            InputTokens = i,
            OutputTokens = o,
            Cost = (i * profile.InputCostPerMillion + o * profile.OutputCostPerMillion) / 1_000_000m
        });
        await db.SaveChangesAsync(ct);
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

    public static int EstimateInput(IEnumerable<ChatMessage> messages, IList<AITool>? tools) =>
        messages.Sum(x => x.Text.Length + 16 + (x.AdditionalProperties?.TryGetValue("reasoning_content", out var reasoning) == true ? reasoning?.ToString()?.Length ?? 0 : 0) +
            x.Contents.Sum(c => c switch
            {
                FunctionCallContent call => Json.Write(call.Arguments).Length + call.Name.Length + 32,
                FunctionResultContent result => (result.Result is string text ? text.Length : Json.Write(result.Result).Length) + 32,
                TextReasoningContent reasoningContent => reasoningContent.Text?.Length ?? 0,
                _ => 0
            })) + (tools?.OfType<AIFunctionDeclaration>().Sum(t => t.JsonSchema.GetRawText().Length + t.Description.Length + t.Name.Length + 32) ?? 0);

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
        await CheckBudgetAsync(profile, text.Length, ct);
        using var client = CompatibleHttp.Configure(http.CreateClient("models"), profile.Endpoint,
            secrets.Unprotect(profile.ProtectedApiKey));
        using IEmbeddingGenerator<string, Embedding<float>> generator =
            new CompatibleEmbeddingGenerator(client, profile.Model, profile.EmbeddingDimensions);
        var result = await generator.GenerateAsync([text], cancellationToken: ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Usage.Add(new UsageRecord
        {
            BotId = profile.BotId,
            ModelProfileId = profile.Id,
            TaskKind = TaskKind.Embedding,
            InputTokens = text.Length,
            Cost = text.Length * profile.InputCostPerMillion / 1_000_000m
        });
        await db.SaveChangesAsync(ct);
        return result[0].Vector.ToArray();
    }

    private async Task CheckBudgetAsync(ModelProfile profile, long reservation, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bot = await db.Bots.AsNoTracking().SingleAsync(x => x.Id == profile.BotId, ct);
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var month = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthly = await db.Usage.Where(x => x.BotId == bot.Id && x.CreatedAt >= month)
            .SumAsync(x => x.InputTokens + x.OutputTokens, ct);
        var daily = await db.Usage.Where(x => x.BotId == bot.Id && x.CreatedAt >= today)
            .SumAsync(x => x.InputTokens + x.OutputTokens, ct);
        if (daily + reservation > bot.DailyTokenLimit || monthly + reservation > bot.MonthlyTokenLimit)
            throw new ModelConfigurationException("Bot token budget exhausted.");
    }
}
