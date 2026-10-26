using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using Wonder.Core;

namespace Wonder.Infrastructure;

public sealed record LoadedImage(byte[] Bytes, string ContentType);

public sealed class MediaStore(
    IDbContextFactory<WonderDbContext> factory,
    IConfiguration configuration,
    IHttpClientFactory clients,
    HybridCache cache)
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
            return new LoadedImage(await cache.GetOrCreateAsync("media:" + existing.Path,
                async token => await File.ReadAllBytesAsync(existing.Path, token),
                new HybridCacheEntryOptions
                    { Expiration = TimeSpan.FromMinutes(1), LocalCacheExpiration = TimeSpan.FromMinutes(1) },
                cancellationToken: ct), existing.ContentType);
        using var client = clients.CreateClient("media");
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
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)),
            ContentType = type,
            Size = bytes.Length,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            File.Delete(file);
            throw;
        }

        return new LoadedImage(bytes, type);
    }

    public static string? ImageType(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            (long)codec.Info.Width * codec.Info.Height > 40_000_000) return null;
        return codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Gif => "image/gif",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => null
        };
    }

    public static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                // Allow private and FakeIP addresses; IsPublic remains available for future use.
                if (addresses.Length == 0)
                    throw new HttpRequestException("Image host did not resolve to any addresses.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }
}
