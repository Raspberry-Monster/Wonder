using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Core.Models;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots/{id:guid}/binding")]
public sealed class BindingsController(WonderDbContext db, MilkyConnectionManager connections, SecretProtector secrets)
    : WonderControllerBase(db)
{
    [HttpGet]
    public async Task<ActionResult<BindingResponse?>> Get(Guid id, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        var binding = await Db.Bindings.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == id, ct);
        return Ok(binding is null
            ? null
            : new BindingResponse(binding.Id, binding.PlatformAccountId, binding.ApiBaseUrl,
                binding.Enabled, binding.ProtectedAccessToken.Length > 0, connections.GetStatus(binding.Id)));
    }

    [HttpPut]
    public async Task<IActionResult> Update(Guid id, BindingInput input, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        var binding = await Db.Bindings.SingleOrDefaultAsync(x => x.BotId == id, ct);
        if (binding is null)
        {
            binding = new PlatformBinding { BotId = id };
            Db.Bindings.Add(binding);
        }

        if (await Db.Bindings.AnyAsync(x => x.Id != binding.Id && x.PlatformAccountId == input.PlatformAccountId, ct))
            return Conflict(new { error = "该 QQ 账号已用于其他 Bot。" });
        var apiBaseUrl = input.ApiBaseUrl.TrimEnd('/');
        var changed = binding.PlatformAccountId != input.PlatformAccountId || binding.ApiBaseUrl != apiBaseUrl ||
                      input.ClearAccessToken || !string.IsNullOrEmpty(input.AccessToken);
        // Never forward a saved credential to a newly configured server or account.
        if (binding.PlatformAccountId != input.PlatformAccountId || binding.ApiBaseUrl != apiBaseUrl ||
            input.ClearAccessToken)
            binding.ProtectedAccessToken = "";
        if (!string.IsNullOrEmpty(input.AccessToken)) binding.ProtectedAccessToken = secrets.Protect(input.AccessToken);
        binding.AdapterType = "milky";
        binding.PlatformAccountId = input.PlatformAccountId;
        binding.ApiBaseUrl = apiBaseUrl;
        binding.Enabled = input.Enabled;
        await Db.SaveChangesAsync(ct);
        if (changed || !input.Enabled) connections.Disconnect(binding.Id);
        return NoContent();
    }
}

public sealed record BindingResponse(
    Guid Id,
    string PlatformAccountId,
    string ApiBaseUrl,
    bool Enabled,
    bool HasAccessToken,
    PlatformLoginStatus Login);