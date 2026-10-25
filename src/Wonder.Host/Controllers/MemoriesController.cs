using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/me")]
public sealed class MemoriesController(WonderDbContext db, MemoryService service) : WonderControllerBase(db)
{
    [HttpGet("memory-settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict(new { error = "Link QQ identity first." });
        return Ok(await Db.LogicalUsers.AsNoTracking().Where(x => x.Id == id).Select(x => new
            {
                x.AllowMemory, x.PreferredName,
                x.AllowMemoryMerge, x.AllowMemoryUpdate, x.AllowMemoryArchive, x.RetainMemoryHistory
            })
            .SingleAsync(ct));
    }

    [HttpPut("memory-settings")]
    public async Task<IActionResult> UpdateSettings(MemorySettingsInput input, CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        await service.UpdateSettingsAsync(id.Value, input.AllowMemory, input.PreferredName,
            input.AllowMemoryMerge, input.AllowMemoryUpdate, input.AllowMemoryArchive, input.RetainMemoryHistory, ct);
        return NoContent();
    }

    [HttpGet("memories")]
    public async Task<IActionResult> List(int? skip, CancellationToken ct, bool includeArchived = false)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        var roots = await Db.Memories.AsNoTracking().Where(x =>
                x.SubjectId == id && x.Status != MemoryStatus.Revoked &&
                (x.ArchivedAt == null || (includeArchived && x.Text != "")))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(Math.Clamp(skip ?? 0, 0, 100000)).Take(100)
            .Select(x => new
            {
                x.Id, x.BotId, x.FactKey, x.Text, x.Status, x.CreatedAt,
                x.ArchivedAt, x.SupersedesMemoryId, x.LastConfirmedAt, x.Revision
            }).ToListAsync(ct);
        return Ok(roots);
    }

    [HttpGet("memories/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        var memory = await Db.Memories.AsNoTracking()
            .Where(x => x.Id == id && x.SubjectId == subject && x.Status != MemoryStatus.Revoked)
            .Select(x => new
            {
                x.Id, x.BotId, x.Text, x.FactKey, x.Status,
                x.ArchivedAt, x.SupersedesMemoryId, x.LastConfirmedAt, x.Revision
            })
            .SingleOrDefaultAsync(ct);
        return memory is null ? NotFound() : Ok(memory);
    }

    [HttpGet("memory-maintenance")]
    public async Task<IActionResult> Maintenance(int? skip, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return Conflict();
        return Ok(await Db.MemoryCandidates.AsNoTracking()
            .Where(x => x.SubjectId == subject && x.Status != MemoryMaintenanceStatus.Cancelled)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(Math.Clamp(skip ?? 0, 0, 100000)).Take(100)
            .Select(x => new
            {
                x.Id, x.BotId, x.Text, x.Status, x.Action, x.Reason,
                x.TargetMemoryId, x.ResultMemoryId, x.CreatedAt, x.CompletedAt
            }).ToListAsync(ct));
    }

    [HttpDelete("memories/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        await service.RevokeAsync(subject.Value, id, null, ct);
        return NoContent();
    }

    [HttpPatch("memories/{id:guid}")]
    public async Task<IActionResult> Correct(Guid id, CorrectMemory input, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        await service.RevokeAsync(subject.Value, id, input.Text, ct);
        return NoContent();
    }
}
