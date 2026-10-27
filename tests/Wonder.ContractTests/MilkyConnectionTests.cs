using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Wonder.Core;
using Wonder.Core.Exceptions;
using Wonder.Core.Models;
using Wonder.Host.Milky;

namespace Wonder.ContractTests;

public sealed class MilkyConnectionTests
{
    [Test]
    [Arguments("")]
    [Arguments("/proxy/bot")]
    public async Task MilkyAuthenticatesBothTransportsReceivesFragmentsAndSendsScopedReceipts(string prefix)
    {
        await using var server = await MilkyServer.StartAsync(prefix);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new TaskCompletionSource<NormalizedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        await using var client = new MilkyConnection(123, server.Url, "test-token", transport,
            (message, _) => { received.TrySetResult(message); return Task.CompletedTask; }, stop.Token);
        await client.ConnectAsync();
        await Assert.That(client.Status.State).IsEqualTo(PlatformLoginState.Online);
        server.Events.Writer.TryWrite("""
            {"self_id":999,"event_type":"bot_offline","time":1792454400,"data":{}}
            """);
        server.Events.Writer.TryWrite("""
            {"self_id":123,"event_type":"message_receive","data":{"message_scene":"friend",
            "peer_id":456,"sender_id":456,"message_seq":88,"time":1792454400,
            "segments":[{"type":"text","data":{"text":"你好"}}]}}
            """);
        var message = await received.Task.WaitAsync(stop.Token);
        await Assert.That(message.MessageId).IsEqualTo("private:456:88");
        await Assert.That(message.Segments.Single().Value).IsEqualTo("你好");
        await Assert.That(await client.SendAsync(ConversationKind.Private, "456", "hello", stop.Token)).IsEqualTo("private:456:99");
        await Assert.That(await client.SendAsync(ConversationKind.Group, "789", "group", stop.Token)).IsEqualTo("group:789:99");
        var requests = server.Requests.ToArray();
        await Assert.That(requests.All(x => x.Token == "Bearer test-token")).IsTrue();
        await Assert.That(requests.Any(x => x.Path == prefix + "/event")).IsTrue();
        await Assert.That(requests.Count(x => x.Path == prefix + "/api/get_login_info")).IsEqualTo(2);
        using var body = JsonDocument.Parse(requests.Single(x => x.Path == prefix + "/api/send_private_message").Body);
        await Assert.That(body.RootElement.GetProperty("user_id").GetInt64()).IsEqualTo(456);
        await Assert.That(body.RootElement.GetProperty("message")[0].GetProperty("data").GetProperty("text").GetString()).IsEqualTo("hello");
        server.SendFailure = "api";
        await Assert.That(async () => { await client.SendAsync(ConversationKind.Private, "456", "uncertain", stop.Token); })
            .ThrowsExactly<DeliveryUnknownException>();
        await Assert.That(server.SendCount).IsEqualTo(3);
        server.Events.Writer.TryWrite("""{"self_id":123,"event_type":"bot_offline","data":{}}""");
        await Assert.That(() => client.Completion.WaitAsync(stop.Token)).ThrowsExactly<HttpRequestException>();
        await Assert.That(client.Status.State).IsEqualTo(PlatformLoginState.Disconnected);
        await Assert.That(async () => { await client.SendAsync(ConversationKind.Private, "456", "offline", stop.Token); })
            .ThrowsExactly<HttpRequestException>();
    }

    [Test]
    [Arguments("http")]
    [Arguments("abort")]
    [Arguments("malformed")]
    [Arguments("empty")]
    [Arguments("missing-sequence")]
    [Arguments("negative-sequence")]
    public async Task MilkyDoesNotRetryUnconfirmedSends(string failure)
    {
        await using var server = await MilkyServer.StartAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        await using var client = new MilkyConnection(123, server.Url, "test-token", transport, (_, _) => Task.CompletedTask, stop.Token);
        await client.ConnectAsync();
        server.SendFailure = failure;
        await Assert.That(async () => { await client.SendAsync(ConversationKind.Private, "456", "uncertain", stop.Token); })
            .ThrowsExactly<DeliveryUnknownException>();
        await Assert.That(server.SendCount).IsEqualTo(1);
    }

    [Test]
    public async Task MilkyWaitsForHandshakeAndStopsAnIdleConnectionOnCancellation()
    {
        await using var server = await MilkyServer.StartAsync();
        server.AllowHandshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        await using var client = new MilkyConnection(123, server.Url, "test-token", transport, (_, _) => Task.CompletedTask, stop.Token);
        var connecting = client.ConnectAsync();
        await server.HandshakeRequested.Task.WaitAsync(stop.Token);
        await Assert.That(client.Status.State).IsEqualTo(PlatformLoginState.Connecting);
        await Assert.That(async () => { await client.SendAsync(ConversationKind.Private, "456", "early", stop.Token); })
            .ThrowsExactly<HttpRequestException>();
        await Assert.That(server.SendCount).IsEqualTo(0);
        server.AllowHandshake.SetResult();
        await connecting;
        await Assert.That(client.Status.State).IsEqualTo(PlatformLoginState.Online);
        await stop.CancelAsync();
        await Assert.That(() => client.Completion).Throws<OperationCanceledException>();
        await Assert.That(client.Status.State).IsEqualTo(PlatformLoginState.Disconnected);
    }

    [Test]
    public async Task MilkyRejectsAccountChangesDuringSubscription()
    {
        await using var server = await MilkyServer.StartAsync();
        server.ChangeAccountOnSubscribe = true;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        await using var client = new MilkyConnection(123, server.Url, "test-token", transport, (_, _) => Task.CompletedTask, stop.Token);
        await Assert.That(() => client.ConnectAsync()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(client.Status.State).IsEqualTo(PlatformLoginState.Disconnected);
        await Assert.That(async () => { await client.SendAsync(ConversationKind.Private, "456", "wrong account", stop.Token); })
            .ThrowsExactly<HttpRequestException>();
        await Assert.That(server.SendCount).IsEqualTo(0);
    }

    [Test]
    public async Task MilkyRejectsFailedWebSocketAuthentication()
    {
        await using var server = await MilkyServer.StartAsync();
        server.RejectEvents = true;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        await using var client = new MilkyConnection(123, server.Url, "test-token", transport, (_, _) => Task.CompletedTask, stop.Token);
        await Assert.That(() => client.ConnectAsync()).ThrowsExactly<WebSocketException>();
        await Assert.That(client.Status.State).IsEqualTo(PlatformLoginState.Disconnected);
        await Assert.That(server.SendCount).IsEqualTo(0);
    }

    [Test]
    public async Task MilkyAwaitsIngestionInOrderAndDisconnectsOnFailure()
    {
        await using var server = await MilkyServer.StartAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<string>();
        using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        await using var client = new MilkyConnection(123, server.Url, "test-token", transport, async (message, ct) =>
        {
            seen.Enqueue(message.MessageId);
            if (message.MessageId == "private:456:1")
            {
                first.SetResult();
                await release.Task.WaitAsync(ct);
            }
            else throw new InvalidOperationException("Ingestion failed.");
        }, stop.Token);
        await client.ConnectAsync();
        for (var seq = 1; seq <= 2; seq++)
            server.Events.Writer.TryWrite($$$"""
                {"self_id":123,"event_type":"message_receive","data":{"message_scene":"friend",
                "peer_id":456,"sender_id":456,"message_seq":{{{seq}}},"time":1792454400,"segments":[]}}
                """);
        await first.Task.WaitAsync(stop.Token);
        await Assert.That(seen.Count).IsEqualTo(1);
        release.SetResult();
        await Assert.That(() => client.Completion.WaitAsync(stop.Token)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(seen.SequenceEqual(new[] { "private:456:1", "private:456:2" })).IsTrue();
        await Assert.That(client.Status.State).IsEqualTo(PlatformLoginState.Disconnected);
    }

    [Test]
    public async Task MilkyRejectsWrongAccountAndStoppedConnectionsWithoutSending()
    {
        await using var server = await MilkyServer.StartAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        await using var client = new MilkyConnection(999, server.Url, "test-token", transport, (_, _) => Task.CompletedTask, stop.Token);
        await Assert.That(() => client.ConnectAsync()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(server.Requests.Any(x => x.Path == "/event")).IsFalse();
        await Assert.That(async () => { await client.SendAsync(ConversationKind.Private, "456", "hello", stop.Token); })
            .ThrowsExactly<HttpRequestException>();
        await stop.CancelAsync();
        await Assert.That(() => client.ConnectAsync()).ThrowsExactly<OperationCanceledException>();
        await Assert.That(server.SendCount).IsEqualTo(0);
    }

    private sealed class MilkyServer(WebApplication app, string prefix) : IAsyncDisposable
    {
        public string Url => app.Urls.Single() + prefix;
        public Channel<string> Events { get; } = Channel.CreateUnbounded<string>();
        public ConcurrentQueue<(string Path, string Token, string Body)> Requests { get; } = new();
        public string? SendFailure { get; set; }
        public bool RejectEvents { get; set; }
        public bool ChangeAccountOnSubscribe { get; set; }
        public long LoginAccount { get; set; } = 123;
        public TaskCompletionSource? AllowHandshake { get; set; }
        public TaskCompletionSource HandshakeRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }

        public static async Task<MilkyServer> StartAsync(string prefix = "")
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var server = new MilkyServer(app, prefix);
            app.UseWebSockets();
            app.Use(async (context, next) =>
            {
                var token = context.Request.Headers.Authorization.ToString();
                using var reader = new StreamReader(context.Request.Body);
                server.Requests.Enqueue((context.Request.Path, token, await reader.ReadToEndAsync()));
                if (token != "Bearer test-token") { context.Response.StatusCode = 401; return; }
                await next(context);
            });
            app.MapPost(prefix + "/api/get_login_info", () => Results.Json(new { status = "ok", retcode = 0, data = new { uin = server.LoginAccount, nickname = "test" } }));
            app.MapPost(prefix + "/api/{method}", (HttpContext context) =>
            {
                server.SendCount++;
                if (server.SendFailure == "abort") context.Abort();
                return server.SendFailure switch
                {
                    "api" => Results.Json(new { status = "failed", retcode = -500, data = (object?)null }),
                    "http" => Results.StatusCode(503),
                    "malformed" => Results.Text("{broken-json", "application/json"),
                    "empty" => Results.Json(new { status = "ok", retcode = 0, data = (object?)null }),
                    "missing-sequence" => Results.Json(new { status = "ok", retcode = 0, data = new { time = 1792454400 } }),
                    "negative-sequence" => Results.Json(new { status = "ok", retcode = 0, data = new { message_seq = -1, time = 1792454400 } }),
                    _ => Results.Json(new { status = "ok", retcode = 0, data = new { message_seq = 99, time = 1792454400 } })
                };
            });
            app.Map(prefix + "/event", async context =>
            {
                server.HandshakeRequested.TrySetResult();
                if (server.AllowHandshake is not null) await server.AllowHandshake.Task.WaitAsync(context.RequestAborted);
                if (server.RejectEvents) { context.Response.StatusCode = 401; return; }
                if (server.ChangeAccountOnSubscribe) server.LoginAccount = 999;
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var sending = SendEvents();
                var receiving = socket.ReceiveAsync(new ArraySegment<byte>(new byte[32]), cancel.Token);
                await Task.WhenAny(sending, receiving);
                await cancel.CancelAsync();
                try { await sending; } catch (OperationCanceledException) { }
                try { await receiving; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }

                async Task SendEvents()
                {
                    await foreach (var item in server.Events.Reader.ReadAllAsync(cancel.Token))
                    {
                        var bytes = Encoding.UTF8.GetBytes(item);
                        await socket.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length / 2), WebSocketMessageType.Text, false, cancel.Token);
                        await socket.SendAsync(new ArraySegment<byte>(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2), WebSocketMessageType.Text, true, cancel.Token);
                    }
                }
            });
            await app.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            Events.Writer.TryComplete();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(cancel.Token);
            await app.DisposeAsync();
        }
    }
}
