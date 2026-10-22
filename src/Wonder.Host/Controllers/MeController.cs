using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Host.Contracts;

namespace Wonder.Host.Controllers;

[Route("/api/v1/me")]
public sealed class MeController(WonderDbContext db) : WonderControllerBase(db)
{
    [HttpGet]
    [ProducesResponseType<AccountResponse>(200)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = await Db.Users.AsNoTracking().SingleAsync(x => x.Id == CurrentUserId, ct);
        return Ok(new AccountResponse(user.Id, user.Email, user.LogicalUserId));
    }

    [HttpPost("qq-binding-challenge")]
    public async Task<IActionResult> CreateQqBindingChallenge(CancellationToken ct)
    {
        var user = await Db.Users.SingleAsync(x => x.Id == CurrentUserId, ct);
        if (user.LogicalUserId is not null) return Conflict(new { error = "QQ identity already linked." });
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        await Db.BindingChallenges.Where(x => x.ApplicationUserId == user.Id).ExecuteDeleteAsync(ct);
        Db.BindingChallenges.Add(new BindingChallenge
            { ApplicationUserId = user.Id, CodeHash = Json.Hash(code), ExpiresAt = expiry });
        await Db.SaveChangesAsync(ct);
        return Ok(new { code, expiresAt = expiry, instruction = "通过 QQ 私聊已连接的 Bot，发送 /bind " + code });
    }
}
