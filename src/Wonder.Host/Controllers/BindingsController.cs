using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots/{id:guid}/binding")]
public sealed class BindingsController(WonderDbContext db, SecretProtector secrets) : WonderControllerBase(db)
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        return Ok(await Db.Bindings.AsNoTracking().Where(x => x.BotId == id).Select(x => new
                { x.Id, x.PlatformAccountId, x.WebSocketUrl, x.Enabled, hasAccessToken = x.ProtectedAccessToken != "" })
            .SingleOrDefaultAsync(ct));
    }

    [HttpPut]
    public async Task<IActionResult> Update(Guid id, BindingInput input, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        var binding = await Db.Bindings.SingleOrDefaultAsync(x => x.BotId == id, ct);
        if (binding is null)
        {
            if (string.IsNullOrEmpty(input.AccessToken)) return BadRequest();
            binding = new PlatformBinding { BotId = id };
            Db.Bindings.Add(binding);
        }

        binding.PlatformAccountId = input.PlatformAccountId;
        binding.WebSocketUrl = input.WebSocketUrl;
        binding.Enabled = input.Enabled;
        if (!string.IsNullOrEmpty(input.AccessToken)) binding.ProtectedAccessToken = secrets.Protect(input.AccessToken);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }
}