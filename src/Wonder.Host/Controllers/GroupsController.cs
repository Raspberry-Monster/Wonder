using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots/{id:guid}/groups")]
public sealed class GroupsController(WonderDbContext db) : WonderControllerBase(db)
{
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid id, CancellationToken ct)
    {
        return await FindOwnedBotAsync(id, ct) is null
            ? NotFound()
            : Ok(await Db.GroupPolicies.AsNoTracking().Where(x => x.BotId == id).OrderBy(x => x.GroupId)
                .ThenBy(x => x.Id).Take(200).ToListAsync(ct));
    }

    [HttpGet("{groupId}/policy")]
    public async Task<IActionResult> GetPolicy(Guid id, string groupId, CancellationToken ct)
    {
        return await FindOwnedBotAsync(id, ct) is null
            ? NotFound()
            : Ok(await Db.GroupPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.BotId == id && x.GroupId == groupId, ct));
    }

    [HttpPut("{groupId}/policy")]
    public async Task<IActionResult> PutPolicy(Guid id, string groupId, GroupPolicyInput input, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        if (!long.TryParse(groupId, out var group) || group <= 0) return BadRequest();
        if (input.ContextSpaceId is { } spaceId &&
            !await Db.ContextSpaces.AnyAsync(x => x.Id == spaceId && x.BotId == id, ct))
            return BadRequest(new { error = "Shared space must belong to this Bot." });
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var policy = await Db.GroupPolicies.SingleOrDefaultAsync(x => x.BotId == id && x.GroupId == groupId, ct);
        if (policy is null)
        {
            policy = new GroupPolicy { BotId = id, GroupId = groupId };
            Db.GroupPolicies.Add(policy);
        }

        if (policy.ContextSpaceId != input.ContextSpaceId && policy.ContextSpaceId is { } oldSpace)
        {
            var key = oldSpace.ToString();
            var ids = await Db.Memories
                .Where(x => x.BotId == id && x.Scope == MemoryScope.SpaceShared && x.ScopeOwnerId == key)
                .Select(x => x.Id).ToListAsync(ct);
            await MemoryService.InvalidateSharedContextAsync(Db, id, ids, ct);
        }

        policy.ContextSpaceId = null;
        policy.RespondToMention = input.RespondToMention;
        policy.RespondToReply = input.RespondToReply;
        policy.CommandPrefix = input.CommandPrefix;
        policy.PersistMessages = input.PersistMessages;
        policy.AllowReflection = false;
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(policy);
    }
}
