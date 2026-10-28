using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using SaltyMilky.Net;
using Wonder.Core;
using Wonder.Core.Exceptions;
using Wonder.Core.Models;

namespace Wonder.Host.Milky;

/// <summary>Adapts a SaltyMilky.Net session to Wonder's connection and delivery lifecycle.</summary>
public sealed class MilkyConnection : IAsyncDisposable
{
    private readonly long accountId;
    private readonly MilkyHttpSession client;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime;

    private readonly Channel<MilkyEvent> messages = Channel.CreateBounded<MilkyEvent>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });

    private readonly Func<NormalizedEvent, CancellationToken, Task> onMessage;
    private Task receiving = Task.CompletedTask;
    private readonly ClientWebSocket socket = new();
    private volatile PlatformLoginStatus status = new(PlatformLoginState.Connecting);

    public MilkyConnection(long accountId, string apiBaseUrl, string? accessToken, HttpMessageHandler transport,
        Func<NormalizedEvent, CancellationToken, Task> onMessage, CancellationToken stoppingToken)
    {
        if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        if (!IsValidBaseUrl(apiBaseUrl)) throw new ArgumentException("Invalid Milky base URL.", nameof(apiBaseUrl));
        this.accountId = accountId;
        this.onMessage = onMessage;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var baseUri = new Uri(apiBaseUrl.TrimEnd('/') + "/");
        http = new HttpClient(new ReceiptValidationHandler(transport))
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(30)
        };
        client = new MilkyHttpSession(new MilkyHttpSessionOptions
        {
            HttpClient = http,
            AccessToken = accessToken
        });
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            socket.Options.SetRequestHeader("Authorization", new AuthenticationHeaderValue("Bearer", accessToken).ToString());
        }
    }

    public PlatformLoginStatus Status => status;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await ObserveAsync(receiving);
        await ObserveAsync(Completion);
        status = new PlatformLoginStatus(PlatformLoginState.Stopped);
        socket.Dispose();
        client.Dispose();
        http.Dispose();
        lifetime.Dispose();
    }

    public static bool IsValidBaseUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment) &&
               !uri.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase) &&
               !uri.AbsolutePath.TrimEnd('/').EndsWith("/event", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ConnectAsync()
    {
        lifetime.Token.ThrowIfCancellationRequested();
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        connect.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await VerifyAccountAsync(connect.Token);
            // Own the handshake so an idle stream can become online before its first event.
            await socket.ConnectAsync(MilkyCommunication.ToWebSocketUri(client.EventUri), connect.Token);
            receiving = ReadEventsAsync();

            await VerifyAccountAsync(connect.Token);
            if (receiving.IsCompleted)
            {
                await receiving;
                throw new HttpRequestException("Milky event stream closed while connecting.");
            }

            status = new PlatformLoginStatus(PlatformLoginState.Online);
            Completion = ReceiveAsync();
        }
        catch
        {
            status = new PlatformLoginStatus(PlatformLoginState.Disconnected);
            await lifetime.CancelAsync();
            await ObserveAsync(receiving);
            throw;
        }
    }

    private async Task VerifyAccountAsync(CancellationToken ct)
    {
        var login = await client.GetLoginInfoAsync(ct);
        if (login?.IsSuccess != true || login.Data?.Uin != accountId)
            throw new InvalidOperationException("Milky login account mismatch or QQ is not logged in.");
    }

    public async Task<string> SendAsync(ConversationKind kind, string target, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (status.State != PlatformLoginState.Online || socket.State != WebSocketState.Open || receiving.IsCompleted)
            throw new HttpRequestException("Milky is disconnected.");
        lifetime.Token.ThrowIfCancellationRequested();
        if (!long.TryParse(target, out var peer) || peer <= 0)
            throw new ArgumentException("Invalid QQ target.", nameof(target));
        if (kind is not (ConversationKind.Private or ConversationKind.Group))
            throw new ArgumentOutOfRangeException(nameof(kind));
        MilkyOutgoingSegment[] message = [new MilkyTextSegment(text)];
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = kind == ConversationKind.Group
                ? await client.SendGroupMessageAsync(peer, message, cancel.Token)
                : await client.SendPrivateMessageAsync(peer, message, cancel.Token);
            if (result?.IsSuccess != true || result.Data is null || result.Data.MessageSeq < 0)
                throw new InvalidDataException("Invalid Milky receipt.");
            return MilkyProtocol.MessageKey(kind, target, result.Data.MessageSeq);
        }
        catch (Exception ex)
        {
            // Delivery may have succeeded even if the HTTP response is lost. Never retry automatically.
            throw new DeliveryUnknownException("Milky sending began but its receipt is unconfirmed.", ex);
        }
    }

    private async Task ReadEventsAsync()
    {
        await foreach (var e in MilkyCommunication.ReadWebSocketEventsAsync(socket, lifetime.Token))
        {
            if (e.SelfId != accountId) continue;
            if (e.Data is MilkyBotOfflineEventData) throw new HttpRequestException("QQ logged out in Milky.");
            if (e.Data is MilkyMessageReceiveEventData && !messages.Writer.TryWrite(e))
                throw new IOException("Milky message queue is full.");
        }
    }

    private async Task ReceiveAsync()
    {
        var processing = ProcessMessagesAsync();
        try
        {
            await await Task.WhenAny(receiving, processing);
            lifetime.Token.ThrowIfCancellationRequested();
            throw new HttpRequestException("Milky event stream closed.");
        }
        finally
        {
            status = new PlatformLoginStatus(PlatformLoginState.Disconnected);
            await lifetime.CancelAsync();
            await ObserveAsync(receiving);
            await ObserveAsync(processing);
        }
    }

    private async Task ProcessMessagesAsync()
    {
        await foreach (var e in messages.Reader.ReadAllAsync(lifetime.Token))
        {
            var normalized = MilkyProtocol.Normalize(accountId, e);
            if (normalized is not null) await onMessage(normalized, lifetime.Token);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        } // The connection owner observes the primary failure through Completion/ConnectAsync.
    }

    // The SDK defaults a missing message_seq to zero. Validate the wire receipt before it is
    // deserialized, without taking ownership of the factory's pooled handler.
    private sealed class ReceiptValidationHandler(HttpMessageHandler transport) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(transport, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await invoker.SendAsync(request, ct);
            var path = request.RequestUri!.AbsolutePath;
            if (!response.IsSuccessStatusCode ||
                !(path.EndsWith("/api/send_private_message", StringComparison.Ordinal) ||
                  path.EndsWith("/api/send_group_message", StringComparison.Ordinal))) return response;
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("message_seq", out var sequence) || sequence.ValueKind != JsonValueKind.Number ||
                    !sequence.TryGetInt64(out var value) || value < 0)
                    throw new InvalidDataException("Missing or invalid Milky message sequence.");
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) invoker.Dispose();
            base.Dispose(disposing);
        }
    }
}
