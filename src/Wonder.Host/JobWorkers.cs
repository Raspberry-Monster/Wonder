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
            ConsumeAsync([JobTypes.Chat], ct), ConsumeAsync([JobTypes.Chat], ct),
            ConsumeAsync([JobTypes.Outgoing], ct),
            ConsumeAsync([JobTypes.Reflection, JobTypes.Emotion, JobTypes.Relationship], ct),
            ConsumeAsync([JobTypes.Memory, JobTypes.Embedding, JobTypes.MemoryMaintenance], ct),
            ConsumeAsync([JobTypes.Maintenance, JobTypes.Narrative, JobTypes.Summary, JobTypes.Ledger], ct));
    }

    private async Task ConsumeAsync(string[] types, CancellationToken ct)
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