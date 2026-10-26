using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/me/reflection-runs")]
public sealed class ReflectionRunsController(WonderDbContext db) : WonderControllerBase(db)
{
    [HttpGet]
    public async Task<IActionResult> List(int? skip, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return Conflict();
        return Ok(await ReflectionRunHistory.ReadAsync(Db, subject.Value, null, Math.Clamp(skip ?? 0, 0, 100000), 100,
            ct));
    }

    [HttpGet("{runId:guid}")]
    public async Task<IActionResult> Get(Guid runId, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return Conflict();
        var runs = await ReflectionRunHistory.ReadAsync(Db, subject.Value, null, 0, 1, ct, runId);
        return runs.Length == 0 ? NotFound() : Ok(runs[0]);
    }

    [HttpDelete("{runId:guid}/reason")]
    public async Task<IActionResult> ClearReason(Guid runId, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return Conflict();
        if (!await Db.ReflectionGenerations.AnyAsync(x => x.Id == runId && x.SubjectId == subject, ct))
            return NotFound();
        await Db.AgentReflectionRequests.Where(x => x.GenerationId == runId && x.UserId == subject)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Reason, ""), ct);
        return NoContent();
    }
}