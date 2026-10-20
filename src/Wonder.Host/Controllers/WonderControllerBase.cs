using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[ApiController]
[Authorize]
public abstract class WonderControllerBase(WonderDbContext db) : ControllerBase
{
    protected WonderDbContext Db { get; } = db;
    protected Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    protected Task<BotInstance?> FindOwnedBotAsync(Guid id, CancellationToken ct)
    {
        var ownerId = CurrentUserId;
        return Db.Bots.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct);
    }

    protected Task<Guid?> FindLogicalUserIdAsync(CancellationToken ct)
    {
        var accountId = CurrentUserId;
        return Db.Users.Where(x => x.Id == accountId).Select(x => x.LogicalUserId).SingleAsync(ct);
    }
}