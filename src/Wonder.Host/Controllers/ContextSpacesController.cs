using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots/{id:guid}/context-spaces")]
public sealed class ContextSpacesController(WonderDbContext db) : WonderControllerBase(db)
{
    [HttpGet]
    public async Task<IActionResult> List(Guid id, CancellationToken ct) =>
        await FindOwnedBotAsync(id, ct) is null ? NotFound() :
            Ok(await Db.ContextSpaces.AsNoTracking().Where(x => x.BotId == id).OrderBy(x => x.Name).ThenBy(x => x.Id).Take(200).ToListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create(Guid id, ContextSpaceInput input, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100) return BadRequest();
        var space = new ContextSpace { BotId = id, Name = input.Name.Trim() };
        Db.ContextSpaces.Add(space);
        await Db.SaveChangesAsync(ct);
        return Created($"/api/v1/bots/{id}/context-spaces/{space.Id}", new { space.Id, space.Name });
    }

    [HttpPut("{spaceId:guid}")]
    public async Task<IActionResult> Rename(Guid id, Guid spaceId, ContextSpaceInput input, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100) return BadRequest();
        var space = await Db.ContextSpaces.SingleOrDefaultAsync(x => x.Id == spaceId && x.BotId == id, ct);
        if (space is null) return NotFound();
        space.Name = input.Name.Trim();
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{spaceId:guid}")]
    public async Task<IActionResult> Delete(Guid id, Guid spaceId, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        var space = await Db.ContextSpaces.SingleOrDefaultAsync(x => x.Id == spaceId && x.BotId == id, ct);
        if (space is null) return NotFound();
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var key = spaceId.ToString();
        var memories = await Db.Memories.Where(x => x.BotId == id && x.Scope == MemoryScope.SpaceShared && x.ScopeOwnerId == key)
            .Select(x => x.Id).ToListAsync(ct);
        await MemoryService.InvalidateSharedContextAsync(Db, id, memories, ct);
        await Db.GroupPolicies.Where(x => x.BotId == id && x.ContextSpaceId == spaceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextSpaceId, (Guid?)null), ct);
        Db.ContextSpaces.Remove(space);
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }
}
