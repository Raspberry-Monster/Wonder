using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Platforms;

namespace Wonder.Host;

public sealed class OneBotConnectionManager(
    IDbContextFactory<WonderDbContext> factory,
    SecretProtector secrets,
    MessageIngestor ingestor,
    ILogger<OneBotConnectionManager> logger) : BackgroundService, IPlatformConnection
{
    private readonly ConcurrentDictionary<Guid, Connection> connections = new();
    private readonly Dictionary<Guid, (string Fingerprint, CancellationTokenSource Cancel, Task Task)> runners = [];

    public Task<string> SendAsync(Guid bindingId, ConversationKind kind, string target, string text,
        CancellationToken ct)
    {
        return connections.TryGetValue(bindingId, out var connection)
            ? connection.SendMessageAsync(kind, target, text, ct)
            : throw new HttpRequestException("OneBot is disconnected.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var bindings = await db.Bindings.AsNoTracking()
                        .Where(x => x.Enabled && db.Bots.Any(b => b.Id == x.BotId && b.Enabled))
                        .ToListAsync(stoppingToken);
                    foreach (var stale in runners.Keys.Except(bindings.Select(x => x.Id)).ToArray())
                        await StopRunner(stale);
                    foreach (var binding in bindings)
                    {
                        var fingerprint = Json.Hash(Json.Write(binding));
                        if (runners.TryGetValue(binding.Id, out var old) && old.Fingerprint == fingerprint) continue;
                        if (runners.ContainsKey(binding.Id)) await StopRunner(binding.Id);
                        var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        runners[binding.Id] = (fingerprint, cancel, ConnectLoop(binding, cancel.Token));
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Gateway configuration unavailable: {ErrorType}", ex.GetType().Name);
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var id in runners.Keys.ToArray()) await StopRunner(id);
        }
    }

    private async Task StopRunner(Guid id)
    {
        var runner = runners[id];
        runner.Cancel.Cancel();
        try
        {
            await runner.Task;
        }
        catch (OperationCanceledException)
        {
        }

        runner.Cancel.Dispose();
        runners.Remove(id);
    }

    private async Task ConnectLoop(PlatformBinding binding, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization",
                    "Bearer " + secrets.Unprotect(binding.ProtectedAccessToken));
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(new Uri(binding.WebSocketUrl), ct);
                using var connection = new Connection(socket);
                var receive = connection.ReceiveAsync(async e =>
                {
                    var normalized = OneBotProtocol.Normalize(e);
                    if (normalized is not null) await ingestor.IngestAsync(binding.Id, normalized, ct);
                }, ct);
                try
                {
                    var login = await connection.RequestAsync("get_login_info", new { }, ct);
                    if (login.GetProperty("user_id").ToString() != binding.PlatformAccountId)
                        throw new InvalidOperationException("OneBot login account mismatch.");
                    connection.Verified = true;
                    connections[binding.Id] = connection;
                    failures = 0;
                    logger.LogInformation("OneBot binding {BindingId} connected", binding.Id);
                    await receive;
                }
                finally
                {
                    connections.TryRemove(binding.Id, out _);
                    socket.Abort();
                    try
                    {
                        await receive;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("OneBot binding {BindingId} disconnected: {ErrorType}", binding.Id,
                    ex.GetType().Name);
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)))) +
                TimeSpan.FromMilliseconds(Random.Shared.Next(500)), ct);
        }
    }

    private sealed class Connection(ClientWebSocket socket) : IDisposable
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
        private readonly SemaphoreSlim send = new(1, 1);
        public bool Verified { get; set; }

        public void Dispose()
        {
            send.Dispose();
        }

        public async Task<string> SendMessageAsync(ConversationKind kind, string target, string text,
            CancellationToken ct)
        {
            var parameters = new Dictionary<string, object>
            {
                [kind == ConversationKind.Group ? "group_id" : "user_id"] = long.Parse(target),
                ["message"] = new[] { new { type = "text", data = new { text } } },
                ["auto_escape"] = true
            };
            var data = await RequestAsync(kind == ConversationKind.Group ? "send_group_msg" : "send_private_msg",
                parameters, ct);
            return data.GetProperty("message_id").ToString();
        }

        public async Task<JsonElement> RequestAsync(string action, object parameters, CancellationToken ct)
        {
            var echo = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[echo] = completion;
            var started = false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(Json.Write(new { action, @params = parameters, echo }));
                await send.WaitAsync(ct);
                try
                {
                    if (socket.State != WebSocketState.Open) throw new HttpRequestException("OneBot is disconnected.");
                    started = true;
                    await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
                }
                finally
                {
                    send.Release();
                }

                var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                if (result.TryGetProperty("retcode", out var code) && code.GetInt32() != 0)
                    throw new HttpRequestException("OneBot rejected action.");
                return result.GetProperty("data").Clone();
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex) when (started)
            {
                throw new DeliveryUnknownException("OneBot acknowledgement was not received.", ex);
            }
            finally
            {
                pending.TryRemove(echo, out _);
            }
        }

        public async Task ReceiveAsync(Func<JsonElement, Task> onEvent, CancellationToken ct)
        {
            var buffer = new byte[16384];
            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var data = new MemoryStream();
                    ValueWebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                        if (frame.MessageType == WebSocketMessageType.Close) return;
                        if (frame.MessageType != WebSocketMessageType.Text || data.Length + frame.Count > 1024 * 1024)
                            throw new InvalidDataException("Invalid OneBot frame.");
                        data.Write(buffer, 0, frame.Count);
                    } while (!frame.EndOfMessage);

                    using var doc = JsonDocument.Parse(data.ToArray());
                    var e = doc.RootElement;
                    if (e.TryGetProperty("echo", out var echo) && pending.TryGetValue(echo.ToString(), out var waiter))
                        waiter.TrySetResult(e.Clone());
                    else if (Verified) await onEvent(e);
                }
            }
            finally
            {
                foreach (var waiter in pending.Values)
                    waiter.TrySetException(new DeliveryUnknownException("OneBot connection closed."));
            }
        }
    }
}