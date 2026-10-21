using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Wonder.Core;

namespace Wonder.Infrastructure;

public sealed record LoadedImage(byte[] Bytes, string ContentType);

public sealed class MediaStore(IDbContextFactory<WonderDbContext> factory, IConfiguration configuration)
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    public string Root { get; } = Path.GetFullPath(configuration["Storage:MediaPath"] ?? "data/media");

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 4)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
                   !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
                   !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                   !(b[0] == 198 && b[1] is 18 or 19);
        // Permit globally routable IPv6 only; excludes link-local, ULA, multicast and transition ranges.
        return b.Length == 16 && (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x02) &&
               !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0);
    }

    public async Task<LoadedImage?> LoadAsync(ChatMessage message, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443) return null;
        var key = Json.Hash(url);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Media.Where(x => x.BotId == message.BotId && x.ExpiresAt > DateTimeOffset.UtcNow)
                .SumAsync(x => x.Size, ct) > 128L * 1024 * 1024) return null;
        var existing = await db.Media.AsNoTracking()
            .FirstOrDefaultAsync(x => x.MessageId == message.Id && x.Hash == key && x.ExpiresAt > DateTimeOffset.UtcNow,
                ct);
        if (existing is not null && File.Exists(existing.Path))
            return new LoadedImage(await File.ReadAllBytesAsync(existing.Path, ct), existing.ContentType);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes) return null;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + count > MaximumBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, count), ct);
        }

        var bytes = memory.ToArray();
        var type = ImageType(bytes);
        if (type is null) return null;
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(file, bytes, ct);
        db.Media.Add(new MediaAsset
        {
            BotId = message.BotId,
            MessageId = message.Id,
            Path = file,
            Hash = key,
            ContentType = type,
            Size = bytes.Length,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync(ct);
        return new LoadedImage(bytes, type);
    }

    public static string? ImageType(byte[] bytes)
    {
        return bytes switch
        {
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, ..] => "image/png",
            [0xff, 0xd8, 0xff, ..] => "image/jpeg",
            [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => "image/gif",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
            _ => null
        };
    }
}