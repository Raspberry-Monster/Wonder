using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots/{id:guid}/identity")]
public sealed class PersonaController(WonderDbContext db) : WonderControllerBase(db)
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        return await FindOwnedBotAsync(id, ct) is null
            ? NotFound()
            : Ok(await Db.Identities.AsNoTracking().Where(x => x.BotId == id).OrderByDescending(x => x.Revision)
                .Take(30).ToListAsync(ct));
    }

    [HttpPut]
    public async Task<IActionResult> Update(Guid id, PublishIdentity input, CancellationToken ct)
    {
        var bot = await FindOwnedBotAsync(id, ct);
        if (bot is null) return NotFound();
        var revision = await Db.Identities.Where(x => x.BotId == id).MaxAsync(x => x.Revision, ct) + 1;
        var identity = new PersonaIdentityRevision { BotId = id, Content = input.Content, Revision = revision };
        Db.Identities.Add(identity);
        bot.SettingsRevision++;
        await Db.SaveChangesAsync(ct);
        return Ok(identity);
    }
}