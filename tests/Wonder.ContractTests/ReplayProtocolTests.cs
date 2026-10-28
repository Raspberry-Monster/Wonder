using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Host;
using Wonder.Host.Features.Chat;
using Wonder.Infrastructure.Models;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.ContractTests;

public sealed class ReplayProtocolTests
{
    [Test]
    [Arguments("\"prompt_cache_hit_tokens\":80,\"prompt_cache_miss_tokens\":20", 80L, 20L)]
    [Arguments("\"prompt_tokens_details\":{\"cached_tokens\":70}", 70L, 30L)]
    [Arguments("\"unrelated_count\":1", null, null)]
    public async Task CacheDiagnosticsUseProviderCountsAndLeaveMissingValuesUnknown(string extra, long? hit, long? miss)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"answer\"}}]," +
                "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":1," + extra + "}}", Encoding.UTF8, "application/json")
        }))) { BaseAddress = new Uri("https://example.com/v1/") };
        using var client = new CompatibleChatClient(http, "model", false);
        var response = await client.GetResponseAsync([new AiMessage(ChatRole.User, "hello")]);
        var counts = ChatCacheUsage.Read(response);
        await Assert.That(counts.Cached).IsEqualTo(hit);
        await Assert.That(counts.Uncached).IsEqualTo(miss);
        await Assert.That(response.Usage!.InputTokenCount).IsEqualTo(100);
    }

    [Test]
    public async Task OfficialAdapterRoundTripPreservesOrderedMultimodalMessagesAndRawToolArguments()
    {
        var requests = new List<string>();
        var responseNumber = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync());
            var json = responseNumber++ == 0 ? """
                {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
                "reasoning_content":"required private protocol field","tool_calls":[
                {"id":"one","type":"function","function":{"name":"search","arguments":"{ \"query\" : \"中文\" }"}},
                {"id":"two","type":"function","function":{"name":"search","arguments":"{\"query\":\"other\"}"}}]}}]}
                """ : """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"final"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        })) { BaseAddress = new Uri("https://example.com/v1/") };
        using var client = new CompatibleChatClient(http, "model", false, true);
        var imageBytes = new byte[] { 1, 2, 3 };
        var media = new ReplayMedia(Guid.NewGuid(), "image/png", ReplayMessages.ImageHash(imageBytes));
        var user = new AiMessage(ChatRole.User, [new TextContent("[Quoted reference] 中文"),
            new DataContent(imageBytes, "image/png"), new TextContent("last text block")]);
        List<AiMessage> messages = [new(ChatRole.System, "stable"), user];
        var calls = await client.GetResponseAsync(messages);
        messages.AddRange(calls.Messages);
        messages.Add(new AiMessage(ChatRole.Tool,
            [new FunctionResultContent("one", "{\"records\":[\"中文\"]}"), new FunctionResultContent("two", "{\"records\":[]}")]));
        await client.GetResponseAsync(messages);
        var encoded = messages.Select(x => ReplayMessages.Encode(x, _ => media)).ToArray();
        var serialized = Json.Write(encoded);
        await Assert.That(serialized).DoesNotContain("AQID");
        var restored = new List<AiMessage>();
        foreach (var dto in Json.Read<ReplayMessage[]>(serialized))
            restored.Add(await ReplayMessages.DecodeAsync(dto, reference =>
                Task.FromResult(new DataContent(imageBytes, reference.MediaType))));
        await client.GetResponseAsync(restored);
        using var original = JsonDocument.Parse(requests[1]);
        using var replayed = JsonDocument.Parse(requests[2]);
        var before = original.RootElement.GetProperty("messages");
        var after = replayed.RootElement.GetProperty("messages");
        await Assert.That(after.GetArrayLength()).IsEqualTo(before.GetArrayLength());
        for (var i = 0; i < before.GetArrayLength(); i++)
            await Assert.That(after[i].GetRawText()).IsEqualTo(before[i].GetRawText());
        await Assert.That(after[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString())
            .IsEqualTo("{ \"query\" : \"中文\" }");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
