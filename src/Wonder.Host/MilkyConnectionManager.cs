using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Core.Abstractions;
using Wonder.Core.Models;
using Wonder.Host.Milky;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed class MilkyConnectionManager(
    IDbContextFactory<WonderDbContext> factory,
    SecretProtector secrets,
    MessageIngestor ingestor,
    IHttpMessageHandlerFactory httpHandlers,
    ILogger<MilkyConnectionManager> logger) : BackgroundService, IPlatformConnection
{
    private readonly ConcurrentDictionary<Guid, MilkyConnection> connections = new();

    private readonly ConcurrentDictionary<Guid, (string Fingerprint, CancellationTokenSource Cancel, Task Task)>
        runners = new();

    private readonly ConcurrentDictionary<Guid, PlatformLoginStatus> statuses = new();

    public Task<string> SendAsync(Guid bindingId, ConversationKind kind, string target, string text,
        CancellationToken ct)
    {
        return connections.TryGetValue(bindingId, out var connection)
            ? connection.SendAsync(kind, target, text, ct)
            : throw new HttpRequestException("QQ is disconnected.");
    }

    public PlatformLoginStatus GetStatus(Guid bindingId)
    {
        return connections.TryGetValue(bindingId, out var connection)
            ? connection.Status
            : statuses.GetValueOrDefault(bindingId) ?? new PlatformLoginStatus(PlatformLoginState.Stopped);
    }

    public void Disconnect(Guid bindingId)
    {
        connections.TryRemove(bindingId, out _);
        if (runners.TryGetValue(bindingId, out var runner))
            try
            {
                runner.Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            } // The worker may have already stopped this session.
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
                        .Where(x => x.Enabled && x.AdapterType == "milky" &&
                                    db.Bots.Any(b => b.Id == x.BotId && b.Enabled))
                        .ToListAsync(stoppingToken);
                    foreach (var stale in runners.Keys.Except(bindings.Select(x => x.Id)).ToArray())
                        await StopRunner(stale);
                    foreach (var binding in bindings)
                    {
                        // Changing the endpoint, account or token restarts the connection.
                        var fingerprint = Json.Hash(Json.Write(new
                        {
                            binding.PlatformAccountId, binding.AdapterType, binding.ApiBaseUrl,
                            binding.ProtectedAccessToken
                        }));
                        if (runners.TryGetValue(binding.Id, out var old) && old.Fingerprint == fingerprint &&
                            !old.Cancel.IsCancellationRequested && !old.Task.IsCompleted) continue;
                        if (runners.ContainsKey(binding.Id)) await StopRunner(binding.Id);
                        var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        runners[binding.Id] = (fingerprint, cancel, ConnectLoop(binding, cancel.Token));
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("QQ configuration unavailable: {ErrorType}", ex.GetType().Name);
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
        if (!runners.TryRemove(id, out var runner)) return;
        await runner.Cancel.CancelAsync();
        try
        {
            await runner.Task;
        }
        catch (OperationCanceledException)
        {
        }

        runner.Cancel.Dispose();
        statuses.TryRemove(id, out _);
    }

    private async Task ConnectLoop(PlatformBinding binding, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                statuses[binding.Id] = new PlatformLoginStatus(PlatformLoginState.Connecting);
                // Reload the latest credentials after each disconnect.
                await using var db = await factory.CreateDbContextAsync(ct);
                var current = await db.Bindings.AsNoTracking().SingleAsync(x => x.Id == binding.Id, ct);
                if (current.PlatformAccountId != binding.PlatformAccountId || !current.Enabled) return;
                await using var session = new MilkyConnection(long.Parse(binding.PlatformAccountId), current.ApiBaseUrl,
                    current.ProtectedAccessToken.Length == 0 ? null : secrets.Unprotect(current.ProtectedAccessToken),
                    httpHandlers.CreateHandler("milky"),
                    (message, token) => ingestor.IngestAsync(binding.Id, message, token), ct);
                connections[binding.Id] = session;
                try
                {
                    await session.ConnectAsync();
                    failures = 0;
                    logger.LogInformation("Milky binding {BindingId} connected", binding.Id);
                    await session.Completion.WaitAsync(ct);
                }
                finally
                {
                    connections.TryRemove(binding.Id, out _);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                statuses[binding.Id] = new PlatformLoginStatus(PlatformLoginState.LoginFailed, ex.GetType().Name);
                logger.LogWarning("Milky binding {BindingId} disconnected: {ErrorType}", binding.Id, ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)))) +
                             TimeSpan.FromMilliseconds(Random.Shared.Next(500)), ct);
        }
    }
}