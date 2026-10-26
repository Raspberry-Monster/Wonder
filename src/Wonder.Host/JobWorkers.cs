using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed class JobWorkers(
    JobQueue queue,
    JobProcessor processor,
    ILogger<JobWorkers> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        return Task.WhenAll(
            ConsumeAsync([JobKind.Chat], ct), ConsumeAsync([JobKind.Chat], ct),
            ConsumeAsync([JobKind.Outgoing], ct),
            ConsumeAsync([JobKind.Reflection, JobKind.Emotion, JobKind.Relationship], ct),
            ConsumeAsync([JobKind.Memory, JobKind.Embedding, JobKind.MemoryMaintenance, JobKind.MemoryPrivacy], ct),
            ConsumeAsync([JobKind.Maintenance, JobKind.Summary], ct));
    }

    private async Task ConsumeAsync(JobKind[] types, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
            try
            {
                var job = await queue.TryClaimAsync(types, ct);
                if (job is null) await Task.Delay(TimeSpan.FromSeconds(1), ct);
                else await processor.ProcessAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Task queue deferred: {ErrorType}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
    }
}