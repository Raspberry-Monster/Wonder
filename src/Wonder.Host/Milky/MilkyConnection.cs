using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Milky.Net.Model;
using Wonder.Core;
using Wonder.Core.Exceptions;
using Wonder.Core.Models;
using MilkySdkClient = Milky.Net.Client.MilkyClient;

namespace Wonder.Host.Milky;

/// <summary>Adapts a Milky.Net session to Wonder's connection and delivery lifecycle.</summary>
public sealed class MilkyConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SdkJson = new(MilkyJsonSerializerContext.Default.Options)
    {
        TypeInfoResolver = MilkyJsonSerializerContext.Default.WithAddedModifier(info =>
        {
            // A missing sequence must never become the default value and confirm an unknown delivery.
            if (info.Type == typeof(SendPrivateMessageResponse) || info.Type == typeof(SendGroupMessageResponse))
                info.Properties.Single(p => p.Name == "message_seq").IsRequired = true;
        })
    };

    private readonly long accountId;
    private readonly MilkySdkClient client;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime;

    private readonly Channel<Event<IncomingMessage>> messages = Channel.CreateBounded<Event<IncomingMessage>>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });

    private readonly Func<NormalizedEvent, CancellationToken, Task> onMessage;
    private Task receiving = Task.CompletedTask;
    private ClientWebSocket? socket;
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
        http = new HttpClient(new EndpointHandler(baseUri, transport))
        {
            // SDK 1.2.2 reuses BaseAddress verbatim for ClientWebSocket.ConnectAsync.
            // EndpointHandler maps its HTTP API requests back to the configured HTTP(S) URL.
            BaseAddress = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws" }.Uri,
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (!string.IsNullOrEmpty(accessToken))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client = new MilkySdkClient(http) { JsonSerializerOptions = SdkJson };
        client.Events.MessageReceive += (_, e) =>
        {
            if (e.SelfId == accountId && !messages.Writer.TryWrite(e))
                throw new IOException("Milky message queue is full.");
        };
        client.Events.BotOffline += (_, e) =>
        {
            if (e.SelfId == accountId) throw new HttpRequestException("QQ logged out in Milky.");
        };
    }

    public PlatformLoginStatus Status => status;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await ObserveAsync(receiving);
        await ObserveAsync(Completion);
        status = new PlatformLoginStatus(PlatformLoginState.Stopped);
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
            receiving = client.ReceivingEventUsingWebSocketAsync(ws =>
            {
                socket = ws;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            }, lifetime.Token);
            // The SDK owns the transport; wait for its handshake before allowing sends.
            while (socket?.State != WebSocketState.Open)
            {
                if (receiving.IsCompleted)
                {
                    await receiving;
                    throw new HttpRequestException("Milky event stream closed while connecting.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), connect.Token);
            }

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
        var login = await client.System.GetLoginInfoAsync(ct);
        if (login.Uin != accountId)
            throw new InvalidOperationException("Milky login account mismatch or QQ is not logged in.");
    }

    public async Task<string> SendAsync(ConversationKind kind, string target, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (status.State != PlatformLoginState.Online || socket?.State != WebSocketState.Open || receiving.IsCompleted)
            throw new HttpRequestException("Milky is disconnected.");
        lifetime.Token.ThrowIfCancellationRequested();
        if (!long.TryParse(target, out var peer) || peer <= 0)
            throw new ArgumentException("Invalid QQ target.", nameof(target));
        if (kind is not (ConversationKind.Private or ConversationKind.Group))
            throw new ArgumentOutOfRangeException(nameof(kind));
        OutgoingSegment[] message = [new OutgoingSegment<TextOutgoingSegmentData>(new TextOutgoingSegmentData(text))];
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var sequence = kind == ConversationKind.Group
                ? (await client.Message.SendGroupMessageAsync(new SendGroupMessageRequest(peer, message), cancel.Token))
                .MessageSeq
                : (await client.Message.SendPrivateMessageAsync(new SendPrivateMessageRequest(peer, message),
                    cancel.Token)).MessageSeq;
            if (sequence < 0) throw new InvalidDataException("Invalid Milky receipt.");
            return MilkyProtocol.MessageKey(kind, target, sequence);
        }
        catch (Exception ex)
        {
            // Delivery may have succeeded even if the HTTP response is lost. Never retry automatically.
            throw new DeliveryUnknownException("Milky sending began but its receipt is unconfirmed.", ex);
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

    // Milky.Net uses absolute /api and /event paths. Preserve configured reverse-proxy prefixes
    // for both transports without taking ownership of the factory's pooled handler.
    private sealed class EndpointHandler(Uri baseUri, HttpMessageHandler transport) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(transport, false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new Uri(baseUri, request.RequestUri!.PathAndQuery.TrimStart('/'));
            return invoker.SendAsync(request, ct);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) invoker.Dispose();
            base.Dispose(disposing);
        }
    }
}