using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/me")]
public sealed class MemoriesController(
    WonderDbContext db,
    MemoryService service,
    PrivacyWorkflow privacy) : WonderControllerBase(db)
{
    [HttpGet("memory-capabilities")]
    [ProducesResponseType<MemoryCapabilities>(200)]
    public IActionResult Capabilities()
    {
        return Ok(new MemoryCapabilities(true, true, true, true));
    }

    [HttpGet("memory-settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict(new { error = "Link QQ identity first." });
        return Ok(await Db.LogicalUsers.AsNoTracking().Where(x => x.Id == id).Select(x => new
            {
                x.AllowMemory, x.PreferredName,
                x.AllowMemoryMerge, x.AllowMemoryUpdate, x.AllowMemoryArchive, x.RetainMemoryHistory
            })
            .SingleAsync(ct));
    }

    [HttpPut("memory-settings")]
    public async Task<IActionResult> UpdateSettings(MemorySettingsInput input, CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        await service.UpdateSettingsAsync(id.Value, input.AllowMemory, input.PreferredName,
            input.AllowMemoryMerge, input.AllowMemoryUpdate, input.AllowMemoryArchive, input.RetainMemoryHistory, ct);
        return NoContent();
    }

    [HttpGet("memories")]
    [ProducesResponseType<MemoryView[]>(200)]
    public async Task<IActionResult> List(int? skip, CancellationToken ct, bool includeArchived = false)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        var roots = await Db.Memories.AsNoTracking().Where(x =>
                x.SubjectId == id && x.Status != MemoryStatus.Revoked &&
                (x.ArchivedAt == null || (includeArchived && x.Text != "")))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(Math.Clamp(skip ?? 0, 0, 100000)).Take(100)
            .Select(x => new MemoryView(x.Id, x.BotId, x.FactKey, x.Text, x.Status, x.CreatedAt,
                x.ArchivedAt, x.SupersedesMemoryId, x.LastConfirmedAt, x.Revision, x.IsOutdated)).ToListAsync(ct);
        return Ok(roots);
    }

    [HttpGet("memories/{id:guid}")]
    [ProducesResponseType<MemoryView>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        var memory = await Db.Memories.AsNoTracking()
            .Where(x => x.Id == id && x.SubjectId == subject && x.Status != MemoryStatus.Revoked)
            .Select(x => new MemoryView(x.Id, x.BotId, x.FactKey, x.Text, x.Status, x.CreatedAt,
                x.ArchivedAt, x.SupersedesMemoryId, x.LastConfirmedAt, x.Revision, x.IsOutdated))
            .SingleOrDefaultAsync(ct);
        return memory is null ? NotFound() : Ok(memory);
    }

    [HttpGet("memory-maintenance")]
    public async Task<IActionResult> Maintenance(int? skip, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return Conflict();
        return Ok(await Db.MemoryCandidates.AsNoTracking()
            .Where(x => x.SubjectId == subject && x.Status != MemoryMaintenanceStatus.Cancelled)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(Math.Clamp(skip ?? 0, 0, 100000)).Take(100)
            .Select(x => new
            {
                x.Id, x.BotId, x.Text, x.Status, x.Action, x.Reason,
                x.TargetMemoryId, x.ResultMemoryId, x.CreatedAt, x.CompletedAt
            }).ToListAsync(ct));
    }

    [HttpDelete("memories/{id:guid}")]
    [ProducesResponseType(404)]
    [ProducesResponseType<PrivacyOperationView>(200)]
    [ProducesResponseType<PrivacyOperationView>(202)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> Delete(Guid id,
        [BindRequired] [FromQuery] [Range(1, long.MaxValue)] long expectedRevision,
        [BindRequired] [FromHeader(Name = "Idempotency-Key")]
        string idempotencyKey, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        var result = await privacy.ForgetAsync(subject.Value, id, expectedRevision, idempotencyKey, ct);
        return result.Completed
            ? Ok(result)
            : AcceptedAtAction(nameof(Operation), new { id = result.OperationId }, result);
    }

    [HttpPatch("memories/{id:guid}")]
    [ProducesResponseType<MemoryOperationReceipt>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> Correct(Guid id, CorrectMemory input,
        [BindRequired] [FromHeader(Name = "Idempotency-Key")]
        string idempotencyKey, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        return Ok(await service.EditAsync(subject.Value, id, input.Text, input.ExpectedRevision,
            idempotencyKey, ct));
    }

    [HttpPost("memories/{id:guid}/archive")]
    [ProducesResponseType<MemoryOperationReceipt>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> Archive(Guid id, ArchiveMemory input,
        [BindRequired] [FromHeader(Name = "Idempotency-Key")]
        string idempotencyKey, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        return Ok(await service.ArchiveAsync(subject.Value, id, input.ExpectedRevision, idempotencyKey, ct));
    }

    [HttpGet("memories/{id:guid}/index-state")]
    [ProducesResponseType<MemoryIndexState>(200)]
    public async Task<IActionResult> IndexState(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        var memory = await Db.Memories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.SubjectId == subject &&
            x.Status != MemoryStatus.Revoked, ct);
        if (memory is null) return NotFound();
        var profile = await Db.Models.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BotId == memory.BotId && x.TaskKind == TaskKind.Embedding, ct);
        var ready = profile is not null && await Db.Embeddings.AnyAsync(e =>
            e.MemoryId == id && e.SourceRevision == memory.Revision &&
            Db.EmbeddingProfiles.Any(p => p.Id == e.ProfileId && p.BotId == memory.BotId &&
                                          p.Provider == profile.Endpoint &&
                                          p.Model == profile.Model && p.Dimensions == profile.EmbeddingDimensions &&
                                          p.Version == 1), ct);
        var payload = Json.Write(new IdPayload(id));
        var job = await Db.Outbox.AsNoTracking()
            .Where(x => x.BotId == memory.BotId && x.Type == JobKind.Embedding && x.Payload == payload)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        return Ok(new MemoryIndexState(memory.Revision, memory.ArchivedAt != null ? "Inactive" :
            ready ? "Ready" :
            job?.Status == JobStatus.DeadLetter ? "Failed" :
            job?.Attempts > 0 ? "Retrying" : "Pending", job?.Attempts ?? 0));
    }

    [HttpPost("memories/{id:guid}/retry-index")]
    public async Task<IActionResult> RetryIndex(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        await service.RetryIndexAsync(subject ?? Guid.Empty, id, ct);
        return Accepted();
    }

    [HttpPost("memories/{id:guid}/erase-preview")]
    [ProducesResponseType<ErasePreviewView>(200)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> PreviewErase(Guid id, ErasePreviewInput input, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        return Ok(await privacy.PreviewAsync(subject.Value, id, input.ExpectedRevision, input.MessageIds ?? [], ct));
    }

    [HttpPost("memories/{id:guid}/erase")]
    [ProducesResponseType<PrivacyOperationView>(200)]
    [ProducesResponseType<PrivacyOperationView>(202)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> Erase(Guid id, ConfirmEraseInput input,
        [BindRequired] [FromHeader(Name = "Idempotency-Key")]
        string key, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        var preview = await Db.MemoryOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.PreviewId &&
            x.ActorId == subject &&
            x.TargetMemoryId == id && x.Kind == "ErasePreview", ct);
        if (preview is null) return NotFound();
        if (preview.ExpectedRevision != input.ExpectedRevision) return Conflict();
        var result = await privacy.EraseAsync(subject.Value, input.PreviewId, input.ScopeHash, key, ct);
        return result.Completed
            ? Ok(result)
            : AcceptedAtAction(nameof(Operation), new { id = result.OperationId }, result);
    }

    [HttpGet("memory-operations/{id:guid}")]
    [ProducesResponseType<PrivacyOperationView>(200)]
    public async Task<IActionResult> Operation(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        return subject is null ? NotFound() : Ok(await privacy.GetAsync(subject.Value, id, ct));
    }

    [HttpGet("memory-operations")]
    public async Task<IActionResult> Operations(CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        var ids = await Db.MemoryOperations.AsNoTracking()
            .Where(x => x.ActorId == subject && (x.Kind == "Forget" || x.Kind == "Erase"))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(50).Select(x => x.Id).ToArrayAsync(ct);
        var result = new List<PrivacyOperationView>();
        foreach (var id in ids) result.Add(await privacy.GetAsync(subject.Value, id, ct));
        return Ok(result);
    }

    [HttpPost("memory-operations/{id:guid}/retry")]
    public async Task<IActionResult> RetryPrivacy(Guid id, CancellationToken ct)
    {
        var subject = await FindLogicalUserIdAsync(ct);
        if (subject is null) return NotFound();
        await privacy.RetryAsync(subject.Value, id, ct);
        return AcceptedAtAction(nameof(Operation), new { id }, await privacy.GetAsync(subject.Value, id, ct));
    }
}