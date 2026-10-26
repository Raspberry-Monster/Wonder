using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots/{id:guid}")]
public sealed class BotDiagnosticsController(WonderDbContext db) : WonderControllerBase(db)
{
    [HttpGet("emotion")]
    public async Task<IActionResult> GetEmotion(Guid id, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        return Ok(new
        {
            state = await Db.Emotions.AsNoTracking().SingleAsync(x => x.BotId == id, ct)
        });
    }

    [HttpGet("usage")]
    public async Task<IActionResult> GetUsage(Guid id, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        return Ok(await Db.Usage.AsNoTracking()
            .Where(x => x.BotId == id && x.CreatedAt >= since && x.ReservationExpiresAt == null)
            .GroupBy(x => x.TaskKind)
            .Select(g => new
            {
                taskKind = g.Key, inputTokens = g.Sum(x => x.InputTokens), outputTokens = g.Sum(x => x.OutputTokens),
                cost = g.Sum(x => x.Cost), requests = g.Count(), estimatedRequests = g.Count(x => x.Estimated)
            }).ToListAsync(ct));
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> GetJobs(Guid id, int? skip, CancellationToken ct)
    {
        return await FindOwnedBotAsync(id, ct) is null
            ? NotFound()
            : Ok(await Db.Outbox.AsNoTracking().Where(x => x.BotId == id).OrderByDescending(x => x.Sequence)
                .Skip(Math.Clamp(skip ?? 0, 0, 100000)).Take(100)
                .Select(x => new
                {
                    x.Id, x.Type, x.Status, x.Attempts, x.ErrorCode, x.CreatedAt, x.NextAttemptAt, x.CorrelationId,
                    hasProgress = x.ProgressJson != null
                })
                .ToListAsync(ct));
    }

    [HttpPost("jobs/{jobId:guid}/retry")]
    public async Task<IActionResult> RetryJob(Guid id, Guid jobId, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        var job = await Db.Outbox.SingleOrDefaultAsync(
            x => x.Id == jobId && x.BotId == id && x.Status == JobStatus.DeadLetter, ct);
        if (job is null) return NotFound();
        job.Status = JobStatus.Pending;
        job.Attempts = 0;
        job.ErrorCode = null;
        job.LeaseId = null;
        job.LeaseUntil = null;
        job.NextAttemptAt = DateTimeOffset.UtcNow;
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("deliveries")]
    public async Task<IActionResult> GetDeliveries(Guid id, CancellationToken ct)
    {
        return await FindOwnedBotAsync(id, ct) is null
            ? NotFound()
            : Ok(await (from p in Db.Parts.AsNoTracking()
                join b in Db.Batches on p.BatchId equals b.Id
                where b.BotId == id &&
                      (p.Status == DeliveryStatus.DeliveryUnknown || p.Status == DeliveryStatus.Sending)
                orderby b.Id, p.Sequence, p.Id
                select new { p.Id, p.BatchId, p.Sequence, p.Status, p.ExternalId }).Take(100).ToListAsync(ct));
    }

    [HttpPost("deliveries/{partId:guid}/resolve")]
    public async Task<IActionResult> ResolveDelivery(Guid id, Guid partId, DeliveryResolution input,
        CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        var part = await (from p in Db.Parts
            join b in Db.Batches on p.BatchId equals b.Id
            where b.BotId == id && p.Id == partId && p.Status == DeliveryStatus.DeliveryUnknown
            select p).SingleOrDefaultAsync(ct);
        if (part is null) return NotFound();
        if (input.Action == DeliveryResolutionAction.ConfirmSent)
        {
            part.Status = DeliveryStatus.Sent;
            part.ExternalId = input.ExternalId;
        }
        else if (input.Action == DeliveryResolutionAction.Retry)
        {
            part.Status = DeliveryStatus.Pending;
        }
        else
        {
            return BadRequest();
        }

        await Db.SaveChangesAsync(ct);
        return NoContent();
    }
}
