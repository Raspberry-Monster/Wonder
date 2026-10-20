using Microsoft.AspNetCore.Mvc;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/system")]
public sealed class SystemController(WonderDbContext db, IConfiguration config, BackupService backups)
    : WonderControllerBase(db)
{
    [HttpPost("deletion-ledger")]
    public async Task<IActionResult> ExportDeletionLedger(CancellationToken ct)
    {
        var admins = config.GetSection("SystemAdminIds").Get<string[]>() ?? [];
        if (!admins.Contains(CurrentUserId.ToString(), StringComparer.OrdinalIgnoreCase)) return StatusCode(403);
        return Ok(await backups.ExportLedgerAsync(ct));
    }

    [HttpPost("backups")]
    public async Task<IActionResult> CreateBackup(CancellationToken ct)
    {
        var admins = config.GetSection("SystemAdminIds").Get<string[]>() ?? [];
        if (!admins.Contains(CurrentUserId.ToString(), StringComparer.OrdinalIgnoreCase)) return StatusCode(403);
        return Ok(await backups.CreateAsync(ct));
    }
}