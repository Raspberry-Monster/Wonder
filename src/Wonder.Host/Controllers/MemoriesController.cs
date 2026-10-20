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
        return Ok(await Db.LogicalUsers.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.AllowMemory, x.AllowCrossGroupMemory, x.PreferredName })
            .SingleAsync(ct));
    }

    [HttpPut("memory-settings")]
    public async Task<IActionResult> UpdateSettings(MemorySettingsInput input, CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        await service.UpdateSettingsAsync(id.Value, input.AllowMemory, input.AllowCrossGroupMemory, input.PreferredName, ct);
        return NoContent();
    }

    [HttpGet("memories")]
    public async Task<IActionResult> List(int? skip, CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        var roots = await Db.Memories.AsNoTracking().Where(x => x.SubjectId == id && x.SharedFromMemoryId == null && x.Status != MemoryStatus.Revoked)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(Math.Clamp(skip ?? 0, 0, 100000)).Take(100)
            .Select(x => new { x.Id, x.BotId, x.Scope, x.ScopeOwnerId, x.FactKey, x.Text, x.Status, x.CreatedAt }).ToListAsync(ct);
        var ids = roots.Select(x => x.Id).ToArray();
        var shares = await Db.Memories.AsNoTracking().Where(x => x.SubjectId == id && x.SharedFromMemoryId != null &&
            ids.Contains(x.SharedFromMemoryId.Value) && x.Status != MemoryStatus.Revoked)
            .Select(x => new { x.Id, x.SharedFromMemoryId, x.Scope, x.ScopeOwnerId }).ToListAsync(ct);
        var bots = roots.Select(x => x.BotId).Distinct().ToArray();
        var policies = await Db.GroupPolicies.AsNoTracking().Where(x => bots.Contains(x.BotId))
            .Select(x => new { x.BotId, x.GroupId, x.ContextSpaceId }).ToListAsync(ct);
        return Ok(roots.Select(x => new { x.Id, x.BotId, x.Scope, x.ScopeOwnerId, x.FactKey, x.Text, x.Status, x.CreatedAt,
            sharing = shares.Where(s => s.SharedFromMemoryId == x.Id).Select(s => new
            { s.Id, s.Scope, s.ScopeOwnerId, available = s.Scope == MemoryScope.UserShared ||
                policies.Any(g => g.BotId == x.BotId && g.GroupId == x.ScopeOwnerId && g.ContextSpaceId?.ToString() == s.ScopeOwnerId) }) }));
    }

    [HttpGet("memories/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        var memory = await Db.Memories.AsNoTracking()
            .Where(x => x.Id == id && x.SubjectId == subject && x.Status != MemoryStatus.Revoked)
            .Select(x => new { x.Id, x.BotId, x.Scope, x.ScopeOwnerId, x.Text, x.FactKey, x.Status })
            .SingleOrDefaultAsync(ct);
        return memory is null ? NotFound() : Ok(memory);
    }

    [HttpGet("memories/{id:guid}/sharing-options")]
    public async Task<IActionResult> SharingOptions(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        var memory = await Db.Memories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.SubjectId == subject &&
            x.SharedFromMemoryId == null && x.Status != MemoryStatus.Revoked, ct);
        if (memory is null) return NotFound();
        var space = memory.Scope == MemoryScope.GroupUser
            ? await (from g in Db.GroupPolicies join s in Db.ContextSpaces on g.ContextSpaceId equals (Guid?)s.Id
                where g.BotId == memory.BotId && g.GroupId == memory.ScopeOwnerId && s.BotId == memory.BotId
                select new { s.Id, s.Name }).SingleOrDefaultAsync(ct) : null;
        return Ok(new { space });
    }

    [HttpPost("memories/{id:guid}/sharing")]
    public async Task<IActionResult> Share(Guid id, ShareMemoryInput input, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        var sharedId = await service.ShareAsync(subject.Value, id, input.Scope, ct, input.ContextSpaceId);
        return Ok(new { id = sharedId });
    }

    [HttpDelete("memories/{id:guid}/sharing/{sharedId:guid}")]
    public async Task<IActionResult> Withdraw(Guid id, Guid sharedId, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null || !await Db.Memories.AnyAsync(x => x.Id == sharedId && x.SharedFromMemoryId == id &&
            x.SubjectId == subject && x.Status != MemoryStatus.Revoked, ct)) return NotFound();
        await service.WithdrawAsync(subject.Value, sharedId, ct);
        return NoContent();
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
        if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 1000) return BadRequest();
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        await service.RevokeAsync(subject.Value, id, input.Text, ct);
        return NoContent();
    }
}