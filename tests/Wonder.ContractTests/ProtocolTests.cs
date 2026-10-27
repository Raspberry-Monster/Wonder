using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.ContractTests;

public sealed class ProtocolTests
{
    [Test]
    public async Task OfficialAdapterPreservesTextPartsImagesAndUsage()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async request =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return Ok(
                    """{"choices":[{"message":{"content":"你好"}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""");
            }))
            { BaseAddress = new Uri("https://example.com/v1/") };
        using IChatClient client = new CompatibleChatClient(http, "configured-model", true);
        var message = new AiMessage(ChatRole.User, "hello");
        message.Contents.Add(new DataContent(new byte[] { 1, 2, 3 }, "image/png"));
        var assistant = new AiMessage(ChatRole.Assistant, "previous");
        assistant.Contents.Add(new TextContent(" reply"));
        var response = await client.GetResponseAsync(
            [
                new AiMessage(ChatRole.System, "Return JSON."), assistant, new AiMessage(ChatRole.User, "plain text"),
                message
            ],
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json });
        await Assert.That(response.Text).IsEqualTo("你好");
        await Assert.That(response.Usage!.InputTokenCount).IsEqualTo(10);
        using var sent = JsonDocument.Parse(body!);
        await Assert.That(sent.RootElement.GetProperty("model").GetString()).IsEqualTo("configured-model");
        var messages = sent.RootElement.GetProperty("messages");
        await Assert.That(messages[0].GetProperty("content").GetString()).IsEqualTo("Return JSON.");
        await Assert.That(string.Concat(messages[1].GetProperty("content").EnumerateArray()
                .Select(x => x.GetProperty("text").GetString())))
            .IsEqualTo("previous reply");
        await Assert.That(messages[2].GetProperty("content").GetString()).IsEqualTo("plain text");
        await Assert.That(sent.RootElement.GetProperty("response_format").GetProperty("type").GetString())
            .IsEqualTo("json_object");
        await Assert
            .That(sent.RootElement.GetProperty("messages")[3].GetProperty("content")[1].GetProperty("image_url")
                .GetProperty("url").GetString()).IsEqualTo("data:image/png;base64,AQID");
    }

    [Test]
    public async Task ConfigurationErrorsAreNotRetried()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        })) { BaseAddress = new Uri("https://example.com/v1/") };
        await Assert.That(async () =>
        {
            await new CompatibleChatClient(http, "model", false).GetResponseAsync([
                new AiMessage(ChatRole.User, "hello")
            ]);
        }).ThrowsExactly<ModelConfigurationException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task EmbeddingDimensionMismatchIsRejected()
    {
        string? requestBody = null;
        using var http =
            new HttpClient(new Handler(async request =>
                {
                    requestBody = await request.Content!.ReadAsStringAsync();
                    return Ok("""{"data":[{"index":0,"embedding":[0.1,0.2]}]}""");
                }))
                { BaseAddress = new Uri("https://example.com/v1/") };
        var generator = new CompatibleEmbeddingGenerator(http, "embed", 3);
        await Assert.That(async () => { await generator.GenerateAsync(["text"]); })
            .ThrowsExactly<InvalidOperationException>();
        using var sent = JsonDocument.Parse(requestBody!);
        await Assert.That(sent.RootElement.GetProperty("encoding_format").GetString()).IsEqualTo("float");
        await Assert.That(sent.RootElement.GetProperty("dimensions").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task PostgreSqlSchemaIncludesDurableConstraintsAndVectorExtension()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var sql = db.Database.GenerateCreateScript();
        await Assert.That(sql).Contains("CREATE EXTENSION IF NOT EXISTS vector");
        await Assert.That(sql).Contains("IX_Messages_BotId_Platform_ExternalMessageId");
        await Assert.That(sql).Contains("WHERE \"Status\" = 0");
        await Assert.That(sql).Contains("PK_Inbox");
        await Assert.That(sql).Contains("CK_Mood_Range");
        await Assert.That(db.Database.GetMigrations().Any()).IsTrue();
        await Assert.That(db.Database.HasPendingModelChanges()).IsFalse();
    }

    [Test]
    public async Task LongRetryAfterIsPassedToTheDurableQueue()
    {
        using var http = new HttpClient(new Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return Task.FromResult(response);
            }))
            { BaseAddress = new Uri("https://example.com/v1/") };
        var failure = await Assert.That(async () =>
        {
            await new CompatibleChatClient(http, "test", false).GetResponseAsync([
                new AiMessage(ChatRole.User, "hello")
            ]);
        }).ThrowsExactly<ProviderRetryException>();
        await Assert.That(failure!.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task VectorQueryComposesEfAuthorizationBeforeTopK()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var botId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sql = MemoryQueries.Nearest(db, new MemoryAccess(botId, userId), profileId, [1, 0, 0])
            .ToQueryString();
        await Assert.That(sql).Contains("INNER JOIN");
        await Assert.That(sql).Contains("<=>");
        await Assert.That(sql).Contains("SubjectId");
        await Assert.That(sql).Contains("SubjectId");
        await Assert.That(sql).Contains("ORDER BY");
        await Assert.That(sql).Contains("LIMIT");
        await Assert.That(sql.IndexOf("WHERE", StringComparison.Ordinal) <
                          sql.LastIndexOf("LIMIT", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments(null, null)]
    [Arguments(false, null)]
    [Arguments(true, null)]
    [Arguments(true, "low")]
    [Arguments(true, "high")]
    [Arguments(true, "max")]
    public async Task ThinkingOptionsAreExplicitAndReasoningIsNotReturnedToChat(bool? enabled, string? effort)
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Ok(
                """{"choices":[{"finish_reason":"stop","message":{"reasoning_content":"internal reasoning","content":"final answer"}}],"usage":{"prompt_tokens":12,"completion_tokens":100}}""");
        })) { BaseAddress = new Uri("https://api.deepseek.com/") };
        using var client = new CompatibleChatClient(http, "deepseek-flash", true, enabled, effort);
        var response = await client.GetResponseAsync([new AiMessage(ChatRole.User, "hello")],
            new ChatOptions { MaxOutputTokens = 8192 });
        await Assert.That(response.Text).IsEqualTo("final answer");
        await Assert.That(response.FinishReason).IsEqualTo(ChatFinishReason.Stop);
        await Assert.That(response.Usage!.OutputTokenCount).IsEqualTo(100);
        await Assert.That(response.Messages.SelectMany(x => x.Contents).OfType<TextReasoningContent>().Single().Text)
            .IsEqualTo("internal reasoning");
        using var sent = JsonDocument.Parse(body!);
        var request = sent.RootElement;
        await Assert.That(request.GetProperty("max_tokens").GetInt32()).IsEqualTo(8192);
        await Assert.That(request.TryGetProperty("thinking", out var thinking)).IsEqualTo(enabled.HasValue);
        if (enabled.HasValue)
            await Assert.That(thinking.GetProperty("type").GetString())
                .IsEqualTo(enabled.Value ? "enabled" : "disabled");
        await Assert.That(request.TryGetProperty("reasoning_effort", out var reasoning)).IsEqualTo(effort is not null);
        if (effort is not null) await Assert.That(reasoning.GetString()).IsEqualTo(effort);
    }

    [Test]
    [Arguments("")]
    [Arguments(" \r\n")]
    public async Task EmptyHttpBodyReportsProviderResponseFailure(string body)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Ok(body))))
            { BaseAddress = new Uri("https://model.example/v1/") };
        var error = await Assert.That(async () =>
        {
            await new CompatibleChatClient(http, "model", true)
                .GetResponseAsync([new AiMessage(ChatRole.User, "Return JSON.")]);
        }).ThrowsExactly<ModelResponseException>();
        await Assert.That(error!.Message).Contains("empty HTTP response");
        await Assert.That(error.Message).Contains("chat/completions");
    }

    private static HttpResponseMessage Ok(string json)
    {
        if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonObject wire &&
            wire["choices"] is JsonArray choices)
        {
            foreach (var choice in choices)
                if (choice?["message"] is JsonObject message)
                    message["role"] ??= "assistant";
            json = wire.ToJsonString();
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return handle(request);
        }
    }
}