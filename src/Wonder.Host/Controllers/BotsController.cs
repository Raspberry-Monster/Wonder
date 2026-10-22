using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots")]
public sealed class BotsController(WonderDbContext db) : WonderControllerBase(db)
{
    [HttpGet]
    [ProducesResponseType<BotResponse[]>(200)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        return Ok((await Db.Bots.AsNoTracking().Where(x => x.OwnerId == CurrentUserId).OrderBy(x => x.Name).ThenBy(x => x.Id).Take(100)
            .ToListAsync(ct)).Select(BotResponse.From));
    }

    [HttpPost]
    [ProducesResponseType<BotResponse>(201)]
    public async Task<IActionResult> Create(CreateBot input, CancellationToken ct)
    {
        var bot = new BotInstance { OwnerId = CurrentUserId, Name = input.Name.Trim() };
        Db.Bots.Add(bot);
        Db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id });
        Db.Identities.Add(new PersonaIdentityRevision { BotId = bot.Id, Revision = 1, Content = input.Identity });
        await Db.SaveChangesAsync(ct);
        return Created($"/api/v1/bots/{bot.Id}", BotResponse.From(bot));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<BotResponse>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        return await FindOwnedBotAsync(id, ct) is { } bot ? Ok(BotResponse.From(bot)) : NotFound();
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType<BotResponse>(200)]
    public async Task<IActionResult> Update(Guid id, UpdateBot input, CancellationToken ct)
    {
        var bot = await FindOwnedBotAsync(id, ct);
        if (bot is null) return NotFound();
        bot.Name = input.Name ?? bot.Name;
        bot.Enabled = input.Enabled ?? bot.Enabled;
        bot.PersistMessages = input.PersistMessages ?? bot.PersistMessages;
        bot.MessageRetentionDays = input.MessageRetentionDays ?? bot.MessageRetentionDays;
        bot.PrivateIdleMinutes = input.PrivateIdleMinutes ?? bot.PrivateIdleMinutes;
        bot.GroupIdleMinutes = input.GroupIdleMinutes ?? bot.GroupIdleMinutes;
        bot.DailyTokenLimit = input.DailyTokenLimit ?? bot.DailyTokenLimit;
        bot.MonthlyTokenLimit = input.MonthlyTokenLimit ?? bot.MonthlyTokenLimit;
        bot.SettingsRevision++;
        await Db.SaveChangesAsync(ct);
        return Ok(BotResponse.From(bot));
    }
}
